"""Production wiring complements executable OrientationPdfHarness; not a Windows UI test."""
import pathlib
import re
import subprocess
import sys
import unittest
ROOT = pathlib.Path(__file__).resolve().parents[1]
MAIN = (ROOT / 'MainWindow.xaml.cs').read_text()
XAML = (ROOT / 'MainWindow.xaml').read_text()
UI_PATH = ROOT / 'MainWindow.OrientationPdf.cs'
UI = UI_PATH.read_text() if UI_PATH.exists() else ''
def method(name):
    match = re.search(r'(?:private|internal) [^\n]+ ' + name + r'\([^\n]*\)\s*\{', MAIN)
    assert match, name
    i, depth = match.end(), 1
    while depth:
        depth += (MAIN[i] == '{') - (MAIN[i] == '}')
        i += 1
    return MAIN[match.start():i]
class OrientationPdfWiringTests(unittest.TestCase):
    def test_extracted_lifecycle_probe_matches_production(self):
        generated = ROOT / 'tests/OrientationPdfHarness/OrientationUiProbe.Generated.cs'
        before = generated.read_text()
        subprocess.run([sys.executable, str(ROOT / 'tests/generate_orientation_lifecycle_probe.py')], check=True, capture_output=True)
        self.assertEqual(before, generated.read_text())
    def test_compact_explicit_controls(self):
        for label in ['Open Orientation PDF', 'Save PDF to INS']:
            self.assertIn(label, XAML)
        self.assertIn('x:Name="OrientationPdfPanel"', XAML)
        self.assertIn('WrapPanel', XAML)
    def test_applicability_and_read_only(self):
        self.assertIn('OrientationPdfSession.IsApplicable(_currentInspection)', UI)
        self.assertIn('!_readOnlyMode', UI)
        self.assertIn('UpdateOrientationPdfControls();', method('LoadInspectionFileAsync'))
    def test_exact_template_only_no_import_workflow(self):
        self.assertIn('OrientationPdfSession.OpenTemplate', UI)
        for removed in ['ImportOrientationPdf', 'PickOrientationPdf', 'OpenFileDialog', 'Save As', 'Import PDF']:
            self.assertNotIn(removed, UI + XAML)
    def test_open_does_not_stage_and_autosave_does_not_capture(self):
        self.assertNotIn('.Capture(', UI)
        self.assertNotIn('_orientationPdf.Capture', method('SaveCurrentInspectionInPlace'))
        self.assertIn('SyncCurrentItemFromUI();', UI)
    def test_leave_has_three_choices_and_explicit_save_no_stays(self):
        self.assertIn('MessageBoxButton.YesNoCancel', UI)
        self.assertIn('OrientationPdfDecision.Discard', UI)
        self.assertIn('OrientationPdfDecision.Cancel', UI)
        self.assertIn('FinishOrientationEditing(leaving: false)', UI)
    def test_close_and_navigation_guards(self):
        for name in ['MainWindow_Closing', 'LoadInspectionFileAsync', 'DoSave']:
            self.assertIn('FinishOrientationEditing()', method(name))
        self.assertLess(method('MainWindow_Closing').index('FinishOrientationEditing()'), method('MainWindow_Closing').index('if (_hasUnsavedChanges'))
    def test_update_all_editor_guards(self):
        self.assertIn('FinishOrientationEditing()', method('TryPrepareForAppUpdate'))
        start = MAIN.index('var editorWindows = Application.Current.Windows')
        end = MAIN.index('System.Diagnostics.Process.Start', start)
        self.assertIn('editor.FinishOrientationEditing()', MAIN[start:end])
    def test_save_uses_production_session_lifecycle(self):
        self.assertIn('.TryFinish(decision, TrySaveCurrentInspection)', UI)
        self.assertNotIn('_hasUnsavedChanges = false', UI)
    def test_editor_close_is_explicit_not_process_exit_detection(self):
        self.assertIn('Save and close', UI)
        self.assertNotIn('Save As', UI)
        self.assertIn('Process.Start(_orientationPdf.StartInfo)', UI)
        self.assertNotIn('WaitForExit', UI)
        self.assertIn('WorkingPath', UI)
    def test_selection_not_first_wins(self):
        self.assertIn('candidates.Count > 1', UI)
        self.assertIn('ComboBox', UI)
        self.assertIn('SelectedItem', UI)
if __name__ == '__main__': unittest.main()
