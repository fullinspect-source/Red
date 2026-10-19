using System;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using InspectionEditor.Services;

namespace InspectionEditor
{
    // F-014 R1-R4: files opened from Outbox or Archive are read-only (no edits, no autosave, no Save, no Submit), and the
    // editor header shows "Submitted {LastDBDate}" or "[Not Submitted]". The same switch serves F-012's BWT lock.
    public partial class MainWindow
    {
        internal bool HasUnsavedChanges => _hasUnsavedChanges;

        // F-019 D2 / A-019-03: Remove from device closes this editor without saving first (unsaved edits are kept as a
        // recovery copy by CloseReportWithoutSaving). Returns false when the report is still open (pending PDF edits).
        internal bool CloseForRemoval()
        {
            if (_currentInspection == null) return true;
            CloseReportWithoutSaving();
            if (_currentInspection != null) return false;
            if (_openedAsCompanion) Close();
            return true;
        }

        internal static bool IsReadOnlyFolder(string? filePath)
        {
            string folder = Path.GetFileName(Path.GetDirectoryName(filePath ?? "") ?? "") ?? "";
            return folder.Equals("Outbox", StringComparison.OrdinalIgnoreCase) || folder.Equals("Archive", StringComparison.OrdinalIgnoreCase);
        }

        // R3 (SUB §4): "Submitted" plus LastDBDate as MM/dd/yyyy when set, else "[Not Submitted]".
        private string SubmittedHeaderText()
        {
            string? raw = _saveService.HasFile ? HeaderRules.Text(_saveService.GetHeaderValue("LastDBDate")) : null;
            if (string.IsNullOrWhiteSpace(raw))
                return "[Not Submitted]";
            return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
                ? $"Submitted {date.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture)}"
                : "Submitted";
        }

        // R4: the read-only switch is real: the checklist and the item editor take no input (scrolling still works).
        private void ApplyReadOnlyUi()
        {
            bool editable = !_readOnlyMode;
            ApplyInlineReadOnly();
            StatusBorder.IsEnabled = editable;
            CommentEditorBorder.IsEnabled = editable;
            // Photo add/delete and Copy/Remove also refuse in their handlers; disabling them shows it. Photos can still
            // be browsed.
            PhotoActionsPanel.IsEnabled = editable;
            RightPaneDuplicateActions.IsEnabled = editable;
            // Hands-on pass 2026-10-19: the comment builders write the comment too (trade and location chips, saved
            // comments, AI suggestions).
            foreach (var builder in new[] { PrefixScrollViewer.Parent as UIElement, SuffixScrollViewer.Parent as UIElement, SavedCommentsBorder, GenAiBorder })
                if (builder != null)
                    builder.IsEnabled = editable;
            SaveTemplateButton.IsEnabled = editable && _currentInspection != null;
            ApplyTemplateButton.IsEnabled = editable && ApplyTemplateButton.IsEnabled;
            SaveFileButton.Opacity = editable ? 1.0 : 0.4;
            if (_currentInspection != null && !FileNameText.Text.Contains("Submitted", StringComparison.Ordinal))
                FileNameText.Text = $"{FileNameText.Text}  ·  {SubmittedHeaderText()}";
            UpdateSubmitButton();
        }

        // Hands-on pass 2026-10-19: a read-only report must stay readable. Disabling the whole checklist also disabled the
        // Top and End expanders and every row, so the status, notes and signature of an Outbox/Archive report or a locked
        // BWT could not be shown (F-011 P6, A-011-08) and no item could be opened to see its photos and comments. Rows,
        // grids and section headers stay selectable with every control that could change a value disabled; the Top/End
        // bodies are shown disabled.
        private void ApplyInlineReadOnly()
        {
            InlineChecklistPanel.IsEnabled = true;
            UIElement? previous = null;
            foreach (UIElement child in InlineChecklistPanel.Children)
            {
                bool headerBody = previous is Border { Tag: TopSectionKey or EndSectionKey } && child is Border { Tag: null };
                child.IsEnabled = !_readOnlyMode || !headerBody;
                if (_readOnlyMode && !headerBody)
                    DisableEditControls(child);
                previous = child;
            }
        }

        private static void DisableEditControls(DependencyObject parent)
        {
            foreach (object child in LogicalTreeHelper.GetChildren(parent))
            {
                if (child is ButtonBase or TextBoxBase or Selector or RangeBase or DatePicker or InkCanvas)
                    ((UIElement)child).IsEnabled = false;
                else if (child is DependencyObject element)
                    DisableEditControls(element);
            }
        }
    }
}
