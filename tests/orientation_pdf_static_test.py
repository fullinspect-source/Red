"""Source guards for WPF wiring; executable monitors are covered by production-linked harnesses."""
import pathlib
import re
import subprocess
import sys
import unittest
import xml.etree.ElementTree as ET
ROOT = pathlib.Path(__file__).resolve().parents[1]
MAIN = (ROOT / 'MainWindow.xaml.cs').read_text()
XAML = (ROOT / 'MainWindow.xaml').read_text()
UI = (ROOT / 'MainWindow.OrientationPdf.cs').read_text()
ATTACH = (ROOT / 'MainWindow.Attachments.cs').read_text()
MANAGER = (ROOT / 'AttachmentsWindow.xaml.cs').read_text()
MANAGER_XAML = (ROOT / 'AttachmentsWindow.xaml').read_text()

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
    def test_cleanup_race_retains_remaining_monitors(self):
        self.assertIn('catch (Exception ex)', UI)
        self.assertIn('if (_pdfSessions.Count > 0) _pdfTimer?.Start()', UI)
        self.assertLess(UI.index('entry.Monitor.Cleanup()'), UI.index('_pdfSessions.Remove(entry)'))
        self.assertLess(UI.index('entry.Monitor.Cleanup()'), UI.index('_pdfTimer?.Stop()'))
    def test_poll_refresh_is_change_gated(self):
        self.assertIn('refresh |= captured || before != entry.Monitor.Status', ATTACH)
        self.assertIn('if (refresh) _attachmentsWindow?.RefreshRows()', ATTACH)
    def test_compact_controls_no_second_save(self):
        self.assertIn('Open Orientation PDF', XAML)
        self.assertIn('x:Name="OrientationPdfPanel"', XAML)
        self.assertNotIn('Save PDF to INS', XAML)
        self.assertNotIn('SaveOrientationPdfButton', UI + XAML)
    def test_applicability_and_read_only(self):
        self.assertIn('OrientationPdfSession.IsApplicable(_currentInspection)', UI)
        self.assertIn('!_readOnlyMode', UI)
        self.assertIn('UpdateOrientationPdfControls();', method('LoadInspectionFileAsync'))
    def test_exact_template_only_no_import_workflow(self):
        self.assertIn('OrientationPdfSession.OpenTemplate', UI)
        for removed in ['ImportOrientationPdf', 'PickOrientationPdf', 'OpenFileDialog', 'Save As', 'Import PDF']:
            self.assertNotIn(removed, UI + XAML)
    def test_open_does_not_stage_and_ordinary_save_does_not_capture(self):
        self.assertNotIn('.Capture(', UI)
        self.assertNotIn('_orientationPdf.Capture', method('SaveCurrentInspectionInPlace'))
        self.assertIn('SyncCurrentItemFromUI();', UI)
    def test_leave_has_no_pdf_save_discard_choices(self):
        self.assertNotIn('MessageBoxButton.YesNo', UI)
        self.assertNotIn('OrientationPdfDecision', UI)
        self.assertIn('.CanLeave(out string reason)', UI)
        self.assertIn('Retry Save', UI)
        self.assertIn('cannot detect unsaved edits', UI)
    def test_close_and_navigation_guards(self):
        for name in ['MainWindow_Closing', 'LoadInspectionFileAsync', 'DoSave']:
            self.assertIn('FinishOrientationEditing()', method(name))
        self.assertLess(method('MainWindow_Closing').index('FinishOrientationEditing(leaving: false)'), method('MainWindow_Closing').index('if (_hasUnsavedChanges'))
        self.assertGreater(method('MainWindow_Closing').index('if (!FinishOrientationEditing())'), method('MainWindow_Closing').index('ShowResultPicker('))
    def test_do_save_only_ends_sessions_at_actual_report_exit(self):
        body = method('DoSave')
        self.assertIn('FinishOrientationEditing(leaving: false)', body)
        self.assertLess(body.index('ShowResultPicker('), body.index('if (!FinishOrientationEditing())'))
        self.assertLess(body.index('if (!FinishOrientationEditing())'), body.index('_currentInspection = null'))
    def test_update_all_editor_guards(self):
        self.assertIn('FinishOrientationEditing(leaving: false)', method('TryPrepareForAppUpdate'))
        start = MAIN.index('var editorWindows = Application.Current.Windows')
        end = MAIN.index('System.Diagnostics.Process.Start', start)
        self.assertIn('editor.FinishOrientationEditing(leaving: false)', MAIN[start:end])
        self.assertNotIn('Save or discard', MAIN[start:end])
    def test_monitor_saves_use_guarded_report_path(self):
        self.assertIn('return TrySaveCurrentInspection();', ATTACH)
        self.assertIn('MarkUnsaved();', ATTACH)
        self.assertIn('if (!_pdfSaveInProgress) MessageBox.Show', MAIN)
        self.assertNotIn('_hasUnsavedChanges = false', UI + ATTACH)
    def test_only_disk_monitor_not_process_lifetime(self):
        self.assertIn('Process.Start(_orientationPdf.StartInfo)', UI)
        self.assertNotIn('WaitForExit', UI + ATTACH)
        self.assertIn('DispatcherTimer', ATTACH)
        self.assertIn('.Poll(DateTimeOffset.UtcNow', ATTACH)
    def test_duplicates_block_instead_of_user_guessing(self):
        self.assertIn('OrientationPdfSession.PreferredCandidateIndex(_currentInspection, candidates)', UI)
        self.assertIn('candidates.Count > 1 && !index.HasValue', UI)
        self.assertIn('Contact Trent', UI)
        self.assertIn('OrientationPdfSession.TemplateName(_currentInspection)', UI)
        self.assertNotIn('ComboBox', UI)
    def test_shared_registry_owner_guard_and_owned_root(self):
        self.assertIn('FindPdfMonitor(index)', UI)
        self.assertIn('FindPdfMonitor(index)', ATTACH)
        self.assertIn('ReferenceEquals(entry.Owner, _currentInspection)', ATTACH)
        self.assertIn('entry.InspectionPath != _currentFilePath', ATTACH)
        self.assertIn('Path.Combine(AppIdentity.LocalAppDataPath, "PdfAttachments")', ATTACH)
    def test_manager_is_separate_from_external_documents(self):
        self.assertIn('Content="ATTACHMENTS"', XAML)
        self.assertIn('See All PDFs', XAML)
        for text in ['Open / Edit', 'Add PDFs', 'Delete checked', 'Retry Save', 'Refresh', 'Close']:
            self.assertIn(text, MANAGER_XAML)
        self.assertIn('IsChecked', MANAGER_XAML)
        self.assertIn('Filename', MANAGER_XAML)
        self.assertIn('Status', MANAGER_XAML)
        self.assertIn('not external job documents', MANAGER_XAML)
    def test_manager_confirmation_and_read_only(self):
        self.assertIn('OpenFileDialog', MANAGER)
        self.assertIn('Multiselect = true', MANAGER)
        self.assertIn('15 MiB', MANAGER)
        self.assertIn('bytes:N0', MANAGER)
        self.assertIn('selected.Count', MANAGER)
        self.assertIn('r.Filename', MANAGER)
        self.assertIn('MessageBoxResult.No', MANAGER)
        self.assertIn('RetryButton.IsEnabled = !readOnly', MANAGER)
        self.assertIn('if (_readOnly) return', MANAGER)
    def test_actual_picker_pdf_settings(self):
        ns = {'w': 'http://schemas.microsoft.com/winfx/2006/xaml/presentation'}
        doc = ET.parse(ROOT / 'InspectionPickerWindow.xaml')
        panel = next(e for e in doc.iter() if e.attrib.get('{http://schemas.microsoft.com/winfx/2006/xaml}Name') == 'SettingsPanel')
        choices = {e.attrib.get('Content'): e for e in panel.findall('.//w:RadioButton', ns)}
        default = choices['System default PDF program']
        self.assertEqual(default.attrib['IsChecked'], 'True')
        self.assertEqual(default.attrib['IsEnabled'], 'True')
        self.assertEqual(choices['RED PDF program (coming later)'].attrib['IsEnabled'], 'False')
    def test_version_and_markup(self):
        project = ET.parse(ROOT / 'InspectionEditor.csproj')
        self.assertEqual(project.findtext('./PropertyGroup/Version'), '2.1.40')
        self.assertEqual(project.findtext('./PropertyGroup/ReleaseDate'), '2026-09-22')
        for filename in ['AttachmentsWindow.xaml', 'MainWindow.xaml', 'InspectionPickerWindow.xaml']:
            ET.parse(ROOT / filename)

if __name__ == '__main__': unittest.main()
