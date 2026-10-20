using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using InspectionEditor.Services;
using InspectionEditor.Services.Reports;

namespace InspectionEditor
{
    // F-022 Print: the G1 gate, then the local report preview (ReportPreviewWindow).
    public partial class MainWindow
    {
        private Button? _printButton;

        private void InstallPrintButton()
        {
            if (SaveFileButton.Parent is not Panel panel || _printButton != null)
                return;
            _printButton = new Button { Content = "🖨 Print", IsEnabled = false, ToolTip = "Preview, print, save or e-mail this report" };
            _printButton.Click += (_, _) => PrintCurrentReport();
            // Open, Save, Submit (+ its hint), Check, Print, History.
            UIElement? after = (UIElement?)_completionCheckButton ?? (UIElement?)_submitHint ?? _submitButton;
            int index = after != null ? panel.Children.IndexOf(after) + 1 : panel.Children.IndexOf(SaveFileButton) + 1;
            panel.Children.Insert(index, _printButton);
        }

        private void UpdatePrintButton()
        {
            UpdateHistoryButton(); // F-026
            if (_printButton != null)
                _printButton.IsEnabled = _currentInspection != null && _saveService.HasFile && !_submitRunning;
        }

        private void PrintCurrentReport()
        {
            if (_currentInspection == null || !_saveService.HasFile || _submitRunning)
                return;
            // G1: the open-attachment guard (F-017 G1), then refresh attachments and orientation forms (F-013 step 7),
            // save (not in a read-only report), then the completion check (F-012).
            PollPdfMonitors();
            if (_pdfSessions.Any(entry => ReferenceEquals(entry.Owner, _currentInspection) && !entry.Monitor.CanLeave(out _)))
            {
                MessageBox.Show(this, SubmitService.OpenAttachmentMessage, "Print", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (_readOnlyMode && _hasUnsavedChanges)
            {
                MessageBox.Show(this, "This report is locked, but RED holds unsaved changes for it. Close the report without saving and open it again, then print.",
                    "Print", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!_readOnlyMode && !SaveForSubmit())
                return; // TrySaveCurrentInspection already said why
            if (!PassesCompletionGate(InspectionValidator.PrintRefused, "Print"))
                return;
            Newtonsoft.Json.Linq.JObject document;
            try
            {
                document = ReportService.ParseDocument(_saveService.ProjectCurrent(_currentInspection).ToUtf8Bytes());
            }
            catch (Exception ex)
            {
                DiagnosticLogService.Log("Report preview could not read the inspection", ex);
                MessageBox.Show(this, "The report could not be built. " + ex.Message, "Print", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            new ReportPreviewWindow(document, ReportSettings.Load(), InspectorProfileService.Load()) { Owner = this }.ShowDialog();
        }
    }
}
