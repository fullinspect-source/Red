"""Production wiring complements executable OrientationPdfHarness; not a Windows UI test."""
import pathlib
import re
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
    def test_compact_explicit_controls(self):
        for label in ['Open Orientation PDF', 'Save PDF to INS', 'Import PDF...']:
            self.assertIn(label, XAML)
        self.assertIn('x:Name="OrientationPdfPanel"', XAML)
        self.assertIn('WrapPanel', XAML)
    def test_applicability_and_read_only(self):
        self.assertIn('OrientationPdfSession.IsApplicable(_currentInspection)', UI)
        self.assertIn('!_readOnlyMode', UI)
        self.assertIn('UpdateOrientationPdfControls();', method('LoadInspectionFileAsync'))
    def test_known_template_and_import_fallback(self):
        self.assertIn('OrientationPdfSession.FindTemplate', UI)
        self.assertIn('OpenFileDialog', UI)
        self.assertIn('No Orientation PDF is embedded', UI)
        self.assertIn('TemplateName', UI)
    def test_close_and_navigation_guards(self):
        for name in ['MainWindow_Closing', 'LoadInspectionFileAsync', 'DoSave']:
            self.assertIn('FinishOrientationEditing()', method(name))
        self.assertLess(method('MainWindow_Closing').index('FinishOrientationEditing()'), method('MainWindow_Closing').index('if (_hasUnsavedChanges'))
    def test_update_all_editor_guards(self):
        self.assertIn('FinishOrientationEditing()', method('TryPrepareForAppUpdate'))
        start = MAIN.index('var editorWindows = Application.Current.Windows')
        end = MAIN.index('System.Diagnostics.Process.Start', start)
        self.assertIn('editor.FinishOrientationEditing()', MAIN[start:end])
    def test_save_captures_before_atomic_save_and_marks_dirty(self):
        save = method('SaveCurrentInspectionInPlace')
        self.assertLess(save.index('_orientationPdf.Capture'), save.index('_saveService.Save'))
        self.assertIn('MarkUnsaved();', save)
        self.assertIn('TrySaveCurrentInspection()', UI)
        self.assertLess(UI.index('if (!TrySaveCurrentInspection()) return false;'), UI.index('_orientationPdf.Complete();'))
    def test_editor_close_is_explicit_not_process_exit_detection(self):
        self.assertIn('Save and close', UI)
        self.assertIn('Save As', UI)
        self.assertIn('Process.Start(_orientationPdf.StartInfo)', UI)
        self.assertNotIn('WaitForExit', UI)
        self.assertIn('WorkingPath', UI)
    def test_selection_not_first_wins(self):
        self.assertIn('candidates.Count > 1', UI)
        self.assertIn('ComboBox', UI)
        self.assertIn('SelectedItem', UI)
if __name__ == '__main__': unittest.main()
