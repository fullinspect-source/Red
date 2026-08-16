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

    def test_right_pane_opens_by_default_and_splitter_is_the_collapse_control(self):
        self.assertIn("private bool _selectedItemToolsCollapsed = false;", CODE)
        self.assertIn('x:Name="ChecklistGridSplitter"', XAML)
        self.assertIn('PreviewMouseLeftButtonDown="ChecklistGridSplitter_PreviewMouseLeftButtonDown"', XAML)
        self.assertIn('x:Name="SelectedItemToolsPaneGlyph"', XAML)
        self.assertNotIn('x:Name="SelectedItemToolsPaneButton"', XAML)
        self.assertIn("private void ChecklistGridSplitter_PreviewMouseLeftButtonDown", CODE)
        self.assertIn("if (e.ClickCount >= 2)", CODE)
        self.assertIn("SelectedItemToolsColumn.Width = _selectedItemToolsCollapsed", CODE)
        self.assertIn("? new GridLength(0)", CODE)
        self.assertIn("ChecklistGridSplitter.Visibility = Visibility.Visible;", CODE)
        self.assertIn('? "Click divider to reopen selected-item tools"', CODE)

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

    def test_duplicate_is_a_small_left_edge_action_not_right_pane_header_chrome(self):
        self.assertIn('x:Name="RightPaneNavigation" Visibility="Collapsed"', XAML)
        self.assertIn('x:Name="RightPaneDuplicateActions" Grid.Column="2" Orientation="Horizontal" Visibility="Collapsed"', XAML)
        self.assertIn('x:Name="ItemQuestionText" Visibility="Collapsed"', XAML)
        header_start = CODE.index("private Grid CreateInlineItemHeader")
        header_end = CODE.index("private Border? CreateInlineRoomPressureBalanceChip", header_start)
        header = CODE[header_start:header_end]
        self.assertIn('Content = "[+]"', header)
        self.assertIn("duplicateButton.Click += InlineDuplicateButton_Click;", header)
        self.assertIn("Grid.SetColumn(duplicateButton, 0);", header)
        self.assertNotIn('Content = "Duplicate +"', CODE)

    def test_selected_item_panel_preserves_all_tools(self):
        for control in (
            "PrefixPanel", "SuffixPanel", "CommentsTextBox", "QuickSuggestionsOverlay",
            "SavedCommentsPanel", "PhotoImage", "CameraButton", "GetSuggestionsButton",
            "TranscribeButton",
        ):
            self.assertEqual(XAML.count(f'x:Name="{control}"'), 1, control)

    def test_value_prefix_and_suffix_are_three_independently_scrolled_columns(self):
        self.assertIn('x:Name="SelectedItemEntryGrid"', XAML)
        self.assertIn('x:Name="StatusQuickPanel"', XAML)
        self.assertIn('x:Name="StatusKeyPanel"', XAML)
        self.assertIn('x:Name="StatusPanel" Orientation="Vertical"', XAML)
        self.assertIn('x:Name="PrefixPanel" Orientation="Vertical"', XAML)
        self.assertIn('x:Name="SuffixPanel" Orientation="Vertical"', XAML)
        self.assertIn("StatusQuickPanel.Children.Add(btn);", CODE)
        self.assertIn("StatusKeyPanel.Children.Add(btn);", CODE)

    def test_checklist_number_badges_stay_compact_and_status_choices_cannot_clip(self):
        self.assertIn("InlineEmptyNumberBadgeBrush", CODE)
        self.assertIn("grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });\n            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });", CODE)
        self.assertIn("if (options != null)", CODE)
        self.assertIn("return panel;", CODE)
        self.assertNotIn("MinWidth = 180, MaxWidth = 520", CODE)

    def test_selected_item_comment_preview_hides_and_both_surfaces_share_neon_highlight(self):
        self.assertIn("!ReferenceEquals(item, _editorLoadedItem)", CODE)
        self.assertIn("SelectedItemHighlightColor", CODE)
        self.assertIn("EditorScrollViewer.Background = new SolidColorBrush(SelectedItemHighlightColor);", CODE)
        handler_start = CODE.index("private void InlineItemRow_MouseLeftButtonUp")
        handler_end = CODE.index("private void ToggleInlineItem", handler_start)
        handler = CODE[handler_start:handler_end]
        self.assertIn("PopulateInlineChecklist(SearchFilterBox.Text);", handler)

    def test_right_workspace_scrolls_and_photo_controls_stay_beside_thumbnail(self):
        editor_start = XAML.index('x:Name="EditorScrollViewer"')
        self.assertIn('VerticalScrollBarVisibility="Auto"', XAML[editor_start:editor_start + 400])
        self.assertIn('x:Name="PhotoWorkspaceGrid"', XAML)
        self.assertIn('x:Name="PhotoImage" Grid.Column="1"', XAML)
        self.assertIn('x:Name="NoPhotoPlaceholder" Grid.Column="1"', XAML)
        self.assertIn('x:Name="PhotoActionsPanel" Grid.Column="3"', XAML)
        self.assertLess(XAML.index('x:Name="SavedCommentsBorder"'), XAML.index('x:Name="PhotoBorder"'))
        self.assertLess(XAML.index('x:Name="GenAiBorder"'), XAML.index('x:Name="PhotoBorder"'))
        self.assertIn('x:Name="PhotoBorder" Height="125"', XAML)
        self.assertIn('x:Name="PhotoImage" Grid.Column="1" Width="180" Height="105"', XAML)
        self.assertIn('x:Name="PrevPhotoButton" Grid.Column="0"', XAML)
        self.assertIn('x:Name="NextPhotoButton" Grid.Column="2"', XAML)
        self.assertIn('Visibility="Collapsed"', XAML[XAML.index('x:Name="PrevPhotoButton"'):XAML.index('x:Name="PrevPhotoButton"') + 500])
        self.assertIn("(_currentPhotoIndex - 1 + photoCount) % photoCount", CODE)
        self.assertIn("(_currentPhotoIndex + 1) % photoCount", CODE)
        self.assertIn("IsDescendantOf(source, StatusScrollViewer)", CODE)
        self.assertIn("IsDescendantOf(source, PrefixScrollViewer)", CODE)
        self.assertIn("IsDescendantOf(source, SuffixScrollViewer)", CODE)
        self.assertIn('PanningRatio="1"', XAML)
        self.assertIn('PanningDeceleration="0.001"', XAML)
        self.assertNotIn('PreviewMouseLeftButtonDown="EditorScrollViewer_PreviewMouseLeftButtonDown"', XAML)

    def test_saved_comments_and_ai_have_fixed_heights_with_internal_scrolling(self):
        self.assertIn('x:Name="SavedCommentsBorder" Height="140"', XAML)
        self.assertIn('x:Name="GenAiBorder" Height="150"', XAML)
        self.assertIn('x:Name="SavedCommentsScrollViewer"', XAML)
        self.assertIn('x:Name="SuggestionsScrollViewer"', XAML)

    def test_comment_actions_stack_and_flag_uses_double_on_single_off(self):
        self.assertIn('x:Name="CommentActionsPanel" Grid.Column="1" Orientation="Vertical"', XAML)
        self.assertIn('PreviewMouseLeftButtonDown="SpecialistFlagButton_PreviewMouseLeftButtonDown"', XAML)
        self.assertIn("e.ClickCount >= 2", CODE)
        self.assertIn("CommentWithoutSpecialistFlag(comment)", CODE)
        self.assertIn("RepeatBehavior = RepeatBehavior.Forever", CODE)

    def test_get_three_and_transcription_share_one_output_surface(self):
        self.assertEqual(XAML.count('x:Name="SuggestionsStack"'), 1)
        self.assertIn("🤖 GENERATIVE AI", XAML)
        ai_start = XAML.index("🤖 GENERATIVE AI")
        self.assertIn('<WrapPanel Grid.Row="0" Orientation="Horizontal">', XAML[ai_start - 150:ai_start + 100])
        self.assertIn("await GetAISuggestionsAsync();", CODE)
        self.assertIn("await GetAISuggestionsAsync(transcribeMode: true);", CODE)

    def test_classic_width_cannot_squeeze_the_fixed_tool_pane(self):
        self.assertIn("usableWindowWidth - 430", CODE)
        self.assertIn("new GridLength(classicWidth)", CODE)

    def test_compact_layout_has_versioned_one_time_migration(self):
        self.assertIn("private const double DefaultChecklistFontSize = 16;", CODE)
        self.assertIn('line.StartsWith("CompactSplitLayoutV21=")', CODE)
        self.assertIn('$"CompactSplitLayoutV21=True\\n"', CODE)


if __name__ == "__main__":
    unittest.main()
