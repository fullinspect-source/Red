"""Lifecycle wiring checks; behavioral I/O cases live in SaveLossHarness."""
import pathlib
import unittest
ROOT = pathlib.Path(__file__).resolve().parents[1]
SRC = (ROOT / 'MainWindow.xaml.cs').read_text()
def method(name):
    start = SRC.index('        private ', SRC.index(name) if False else 0)
    import re
    m = re.search(r'        private [^\n]+ ' + re.escape(name) + r'\([^\n]*\)\s*\{', SRC)
    assert m, name
    end = SRC.find('\n        private ', m.end())
    return SRC[m.start():end if end >= 0 else len(SRC)]
class SaveLossTests(unittest.TestCase):
    def test_close_flushes_before_dirty_and_summary_gates(self):
        s = method('MainWindow_Closing')
        self.assertLess(s.index('SyncCurrentItemFromUI();'), s.index('if (_hasUnsavedChanges'))
        self.assertIn('e.Cancel = true;', s[s.index('TrySaveCurrentInspection'):])
    def test_every_inline_text_editor_captures_before_focus_loss(self):
        self.assertEqual(SRC.count('valueBox.TextChanged += InlineValueBox_TextChanged;'), 2)
        self.assertIn('valueCombo.AddHandler(TextBox.TextChangedEvent', SRC)
        self.assertIn('CaptureCommentEdit(item, box.Text)', method('InlineCommentsBox_TextChanged'))
    def test_classic_owner_and_loading_guard(self):
        for name in ['CommentsTextBox_TextChanged', 'StatusTextBox_TextChanged']:
            s = method(name)
            self.assertIn('_isLoadingEditor', s)
            self.assertIn('_editorLoadedItem', s)
        self.assertNotIn('_editorLoadedItem.Comments =', method('SyncCurrentItemFromUI'))
        self.assertNotIn('_editorLoadedItem.Value = StatusTextBox.Text', method('SyncCurrentItemFromUI'))
    def test_focus_persists_and_failed_save_blocks_report_reset(self):
        self.assertIn('Editor_LostKeyboardFocus', SRC)
        self.assertIn('if (!TrySaveCurrentInspection()) return;', method('DoSave'))
        self.assertIn('if (!TrySaveCurrentInspection()) return false;', method('ShowResultPicker'))
    def test_stale_focus_handlers_do_not_replay_old_text(self):
        for name in ['InlineCommentsBox_LostFocus', 'InlineValueBox_LostFocus', 'InlineValueCombo_LostFocus']:
            s = method(name)
            self.assertNotIn('item.Comments =', s)
            self.assertNotIn('item.Value =', s)
            self.assertNotIn('LoadItemEditor', s)
    def test_updater_flushes_before_launch_and_blocks_countdown_edits(self):
        start = SRC.index('var editorWindows = Application.Current.Windows')
        launch = SRC.index('System.Diagnostics.Process.Start', start)
        self.assertIn('editor.SyncCurrentItemFromUI();', SRC[start:launch])
        self.assertIn('!editor.TrySaveCurrentInspection()', SRC[start:launch])
        self.assertIn('editor.IsEnabled = false;', SRC[launch:launch + 650])
    def test_failed_save_is_logged_without_discarding_edits(self):
        s = method('TrySaveCurrentInspection')
        self.assertIn('DiagnosticLogService.Log("Report save failed; edits retained", ex);', s)
        self.assertIn('MarkUnsaved();', s)
        self.assertIn('return false;', s)
        self.assertNotIn('_hasUnsavedChanges = false', s)
    def test_save_does_not_truncate_primary(self):
        s = (ROOT / 'Services/SurgicalSaveService.cs').read_text()
        self.assertNotIn('File.WriteAllText(targetPath, json)', s)
        self.assertIn('AtomicInspectionWriter.Write', s)
if __name__ == '__main__': unittest.main()
