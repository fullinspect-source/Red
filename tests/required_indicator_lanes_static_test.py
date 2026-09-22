#!/usr/bin/env python3
"""Source guards for fixed inline requirement lanes and selected filter styling.
These inspect real WPF construction code; they do not claim native rendering QA.
"""
from pathlib import Path
import re
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
CODE = (ROOT / 'MainWindow.xaml.cs').read_text()
XAML = (ROOT / 'MainWindow.xaml').read_text()


def method(name):
    start = CODE.index(name + '(')
    start = CODE.index('{', start)
    depth = 1
    end = start + 1
    while depth:
        depth += (CODE[end] == '{') - (CODE[end] == '}')
        end += 1
    return CODE[start:end]


class RequirementLaneTests(unittest.TestCase):
    def setUp(self):
        self.header = method('private Grid CreateInlineItemHeader')

    def test_every_row_reserves_two_fixed_rightmost_columns(self):
        columns = re.findall(r'grid.ColumnDefinitions.Add\((.*?)\);', self.header)
        self.assertEqual(len(columns), 9)
        self.assertTrue(all('new GridLength(38)' in col for col in columns[-2:]))
        self.assertIn('Grid.SetColumn(statusControl, 6);', self.header)
        self.assertIn('Grid.SetColumn(photoRequiredButton, 7);', self.header)
        self.assertIn('Grid.SetColumn(valueRequiredBadge, 8);', self.header)
        self.assertIn('new Thickness(2, 0, 2, 1)', method('private Border CreateInlineItemRow'))

    def test_equal_squares_hidden_not_collapsed_with_semantic_predicates(self):
        badges = self.header[self.header.index('var photoRequiredButton'):]
        self.assertEqual(badges.count('Width = 34'), 2)
        self.assertEqual(badges.count('Height = 34'), 2)
        self.assertEqual(badges.count('? Visibility.Visible : Visibility.Hidden'), 2)
        self.assertNotIn('Visibility.Collapsed', badges)
        self.assertIn('Visibility = item.IsPictureRequired && item.Pictures.Count == 0', badges)
        self.assertIn('Visibility = ItemRequirementService.IsPrimaryRequirementMissing(item)', badges)
        self.assertIn('ItemRequirementService.RequiresComment(item) ? "Comment required" : "Value required"', badges)
        self.assertIn('Content = "PIC"', badges)
        self.assertIn('Text = "REQ"', badges)
        self.assertIn('Color.FromRgb(126, 34, 206)', badges)
        self.assertIn('Color.FromRgb(220, 38, 38)', badges)
        self.assertEqual(badges.count('Foreground = Brushes.White'), 2)
        self.assertIn('MinHeight = 48', self.header)

    def test_photo_action_and_other_chips_preserved(self):
        self.assertIn('photoRequiredButton.Click += InlinePhotoRequiredButton_Click;', self.header)
        handler = method('private void InlinePhotoRequiredButton_Click')
        for action in ('LoadItemEditor(item);', 'SelectItemInTreeView(item);', 'CameraButton_Click(sender, e);', 'e.Handled = true;'):
            self.assertIn(action, handler)
        self.assertIn('FindAncestor<ButtonBase>(source) != null', method('private void InlineItemRow_PreviewMouseLeftButtonDown'))
        self.assertIn('$"Photo {item.Pictures.Count}"', self.header)
        self.assertNotIn('"Photo Required"', self.header)
        self.assertNotIn('"Value Required"', self.header)

    def test_value_marker_updates_without_rebuilding_typing_row(self):
        capture = method('private void CaptureValueEdit')
        self.assertIn('UpdateInlinePrimaryRequirementVisuals(item);', capture)
        helper = method('private void UpdateInlinePrimaryRequirementVisuals')
        self.assertIn('border.Name == "InlineValueRequirement"', helper)
        self.assertIn('? Visibility.Visible : Visibility.Hidden', helper)
        self.assertNotIn('RefreshInlineItemRow(item)', capture)

    def test_filter_visuals_derive_from_real_state(self):
        styles = method('private void UpdateChecklistFilterButtonStyles')
        self.assertIn('!_ofiFilterActive && !_reqFilterActive && !_incFilterActive', styles)
        for name, flag in [('Ofi', 'ofi'), ('Req', 'req'), ('Inc', 'inc')]:
            self.assertIn(f'{name}FilterButton, _{flag}FilterActive', styles)
            handler = method(f'private void {name}FilterButton_Click')
            self.assertIn('ResetChecklistFilters();', handler)
            self.assertIn(f'_{flag}FilterActive = true;', handler)
            self.assertLess(handler.index('ResetChecklistFilters();'), handler.index(f'_{flag}FilterActive = true;'))
            self.assertNotIn(f'_{flag}FilterActive = !_{flag}FilterActive;', handler)
            self.assertIn('UpdateChecklistFilterButtonStyles();', handler)
            for other in {'ofi', 'req', 'inc'}:
                self.assertIn(f'_{other}FilterActive = false;', method('private void ResetChecklistFilters'))
        self.assertIn('UpdateChecklistFilterButtonStyles();', method('private void ClearSearchButton_Click'))
        self.assertIn('// Reset filter state and visuals together on new file load.', CODE)

    def test_filter_border_reserves_space_and_matches_right_pane(self):
        styles = method('private static void SetChecklistFilterButtonStyle')
        self.assertIn('selected ? new SolidColorBrush(SelectedItemHighlightColor) : Brushes.Transparent', styles)
        self.assertIn('button.BorderThickness = new Thickness(3);', styles)
        self.assertNotIn('new Thickness(0)', styles)
        self.assertIn('EditorScrollViewer.Background = new SolidColorBrush(SelectedItemHighlightColor);', CODE)
        self.assertIn('SelectedItemHighlightColor = Color.FromRgb(72, 229, 255)', CODE)
        tree = ET.fromstring(XAML)
        names = '{http://schemas.microsoft.com/winfx/2006/xaml}Name'
        controls = {el.get(names): el for el in tree.iter()}
        for name in ['ClearSearchButton', 'OfiFilterButton', 'ReqFilterButton', 'IncFilterButton']:
            self.assertEqual(controls[name].get('BorderThickness'), '3')
            self.assertEqual(controls[name].get('BorderBrush'), '#48E5FF' if name == 'ClearSearchButton' else 'Transparent')


if __name__ == '__main__':
    unittest.main()
