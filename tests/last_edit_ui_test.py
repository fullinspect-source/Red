"""Source/layout guards; these do not execute native WPF."""
import pathlib
import re
import unittest
import xml.etree.ElementTree as ET

ROOT = pathlib.Path(__file__).resolve().parents[1]
CS = (ROOT / 'InspectionPickerWindow.xaml.cs').read_text()
XAML = ET.parse(ROOT / 'InspectionPickerWindow.xaml').getroot()
NS = {'w': 'http://schemas.microsoft.com/winfx/2006/xaml/presentation'}
NAME = '{http://schemas.microsoft.com/winfx/2006/xaml}Name'

def method_between(start, end):
    return CS[CS.index(start):CS.index(end, CS.index(start))]

class LastEditUiTests(unittest.TestCase):
    def test_successful_load_hydrates_after_population(self):
        load = method_between('public async void LoadInspections', 'private InspectionFileInfo? ParseInspectionFile')
        self.assertEqual(load.count('RefreshLastEditLabels(true);'), 1)
        self.assertLess(load.index('_allInspections = loadedInspections;'),
                        load.index('RefreshLastEditLabels(true);'))
        self.assertLess(load.index('RefreshLastEditLabels(true);'), load.index('RefreshList();'))

    def test_activation_before_load_completion_cannot_leave_fallback_row_blank(self):
        # Deterministic lifecycle model driven by the actual load-completion hook.
        # Normal parsing already hydrates metadata. Exercise a fallback row (or
        # transient metadata miss), not the false premise that every parse is blank.
        load = method_between('public async void LoadInspections', 'private InspectionFileInfo? ParseInspectionFile')
        rows = []
        persisted_stamp = 100
        def refresh(read_metadata):
            for row in rows:
                if read_metadata:
                    row['stamp'] = persisted_stamp  # verified-byte result, not mtime
                row['age'] = None if row['stamp'] is None else now - row['stamp']
        now = 160
        refresh(True)  # Activated while asynchronous load is pending: empty snapshot
        rows.append({'stamp': None, 'age': None})  # parser fallback completes later
        if 'RefreshLastEditLabels(true);' in load.split('_allInspections = loadedInspections;', 1)[1]:
            refresh(True)
        for now in (220, 280, 340):
            refresh(False)  # timer must age without doing file I/O
        self.assertEqual(rows[0]['stamp'], persisted_stamp,
                         'early activation + fallback row stays blank without post-load hydration')
        self.assertEqual(rows[0]['age'], 240)

    def test_reload_and_editor_close_share_the_hydration_point(self):
        refresh = method_between('private void RefreshCurrentMyListFolder', 'private bool _isLoading')
        close = method_between('public void NotifyInspectionWindowClosed', 'private ')
        self.assertIn('LoadInspections(_currentFolderPath);', refresh)
        self.assertIn('LoadInspections(_currentFolderPath);', close)
        self.assertNotIn('RefreshLastEditLabels(true)', refresh + close)
        activation = next(line for line in CS.splitlines() if 'Activated +=' in line)
        self.assertRegex(activation, r'Activated\s*\+=.*if\s*\(!_isLoading\)\s*RefreshLastEditLabels\(true\)')

    def test_hidden_load_hydrates_and_reload_invalidates_old_work(self):
        refresh = method_between('private async void RefreshLastEditLabels', 'private void InspectionPickerWindow_Loaded')
        self.assertIn('if (_pickerClosed || (!IsVisible && !readSavedMetadata)) return;', refresh)
        load = method_between('public async void LoadInspections', 'private InspectionFileInfo? ParseInspectionFile')
        self.assertLess(load.index('++_lastEditRefreshGeneration;'), load.index('_allInspections.Clear();'))

    def test_last_column(self):
        grid = XAML.find('.//w:GridView', NS)
        assert grid is not None
        columns = grid.findall('w:GridViewColumn', NS)
        self.assertEqual(columns[-1].get('Header'), 'Last edit')
        self.assertEqual(columns[-1].get(NAME), 'LastEditColumn')
        self.assertIn('"Call", "LastEdit"', CS)

    def test_blank_provenance_is_verified_local_content(self):
        self.assertIn('LastEditUtc = LastEditTime.ReadForFile(filePath, fileBytes)', CS)
        self.assertNotIn('LastEditUtc = File.GetLastWriteTime', CS)
        self.assertIn('saved on this device', CS)

    def test_timer_refresh_does_not_reload_or_read_files(self):
        start = CS.index('private async void RefreshLastEditLabels')
        end = CS.index('private void InspectionPickerWindow_Loaded', start)
        method = CS[start:end]
        self.assertIn('if (!readSavedMetadata) return;', method)
        self.assertLess(method.index('await Task.Run'), method.index('File.ReadAllBytes'))
        self.assertNotIn('LoadInspections(', method)
        self.assertNotIn('RefreshList(', method)
        self.assertIn('generation != _lastEditRefreshGeneration', method)
        self.assertIn('current.Contains(result.Item)', method)
        self.assertIn('_lastEditTimer.Stop()', CS)

    def test_preferred_widths_survive_fitting(self):
        start = CS.index('private void FitColumnsToViewport')
        end = CS.index('private static double FitMinimumWidth', start)
        method = CS[start:end]
        self.assertNotRegex(method, r'_preferredColumnWidths\[.*?\]\s*=')
        self.assertIn('_isApplyingColumnSettings = true', method)
        self.assertIn('ScrollBarVisibility.Auto', method)
        self.assertIn('InspectionListView.SizeChanged', CS)

    def test_width_budget_default_and_minimum(self):
        # Check the source formula plus its numeric budget, not WPF rendering.
        self.assertIn('(available - minimum) / Math.Max(1, preferred - minimum)', CS)
        self.assertIn('min + Math.Max(0, width - min) * fraction', CS)
        columns = {c.get(NAME): float(c.get('Width', '0')) for c in XAML.findall('.//w:GridViewColumn', NS)}
        keys = ['Address', 'Type', 'Date', 'Builder', 'Filename', 'Call', 'LastEdit']
        minimum = [160, 120, 82, 90, 78, 86, 82]
        for window in [1040, 1200, 1400, 1600]:
            available = window - 36 - 18 - 170 - 32 - 4
            for preferred in [[columns[k+'Column'] for k in keys], [430,220,96,180,110,92,82], [900,450,96,400,110,92,82], [40,40,82,40,78,86,82]]:
                widths = [max(m,w) for m,w in zip(minimum, preferred)]
                ratio = min(1, max(0, (available-sum(minimum))/max(1,sum(widths)-sum(minimum))))
                fitted = [m+(w-m)*ratio for m,w in zip(minimum,widths)]
                self.assertLessEqual(sum(fitted), available + 0.001)
                self.assertGreaterEqual(fitted[-1], 82)

if __name__ == '__main__':
    unittest.main()
