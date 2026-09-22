using InspectionEditor.Services;
using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;

namespace InspectionEditor
{
    public partial class MainWindow
    {
        private PdfEditMonitor? _orientationPdf;
        private bool _finishingOrientationPdf;
        private bool _orientationPdfUiBusy;

        private void UpdateOrientationPdfControls()
        {
            bool applicable = OrientationPdfSession.IsApplicable(_currentInspection);
            OrientationPdfPanel.Visibility = applicable ? Visibility.Visible : Visibility.Collapsed;
            OpenOrientationPdfButton.IsEnabled = applicable && !_readOnlyMode;
            AttachmentsButton.IsEnabled = _currentInspection != null;
            OrientationPdfHint.Text = _orientationPdf == null
                ? "Open the exact walk document. Saved PDF changes are automatically embedded, keeping every page."
                : _orientationPdf.Status;
            OrientationPdfHint.ToolTip = _orientationPdf?.WorkingPath;
        }

        private bool SelectOrientationAttachment(out int? index)
        {
            index = null;
            if (_currentInspection == null) return false;
            var candidates = OrientationPdfSession.FindCandidates(_currentInspection);
            index = OrientationPdfSession.PreferredCandidateIndex(_currentInspection, candidates);
            if (candidates.Count > 1 && !index.HasValue)
                throw new InvalidOperationException($"More than one official walk document matches \"{OrientationPdfSession.TemplateName(_currentInspection)}\". " +
                    "Use ATTACHMENTS to inspect and remove extras. RED will not substitute another region's document or guess which copy to use. Contact Trent for the correct walk document.");
            return true;
        }

        private void OpenOrientationPdfButton_Click(object sender, RoutedEventArgs e)
        {
            if (_readOnlyMode || _currentInspection == null || _currentFilePath == null || _orientationPdfUiBusy) return;
            _orientationPdfUiBusy = true;
            try
            {
                // Revalidate the authoritative Template and attachment collection on every click,
                // even when an existing walk/general monitor can be reused.
                if (!SelectOrientationAttachment(out int? index)) return;
                if (_orientationPdf != null && _orientationPdf.AttachmentIndex != index)
                    throw new InvalidOperationException("The official walk attachment changed during editing. Resolve pending PDF changes in ATTACHMENTS before reopening.");
                SyncCurrentItemFromUI();
                if (_orientationPdf == null)
                {
                    _orientationPdf = FindPdfMonitor(index);
                    if (_orientationPdf == null)
                    {
                        var session = index.HasValue
                            ? OrientationPdfSession.OpenEmbedded(_currentInspection, _currentFilePath, index.Value, PdfWorkingRoot)
                            : OrientationPdfSession.OpenTemplate(_currentInspection, _currentFilePath, PdfWorkingRoot);
                        _orientationPdf = session.Monitor;
                        RegisterPdfMonitor(_orientationPdf);
                    }
                }
                // A manager add or formerly ambiguous attachment may have an unprepared monitor.
                // Refuse already-launched unprepared copies rather than clobber editor memory.
                _orientationPdf.PrepareOfficialForOpen();
                UpdateOrientationPdfControls();
                // The default editor can reuse a process. Only stable changed disk bytes count as a save.
                _orientationPdf.MarkLaunched();
                using var process = Process.Start(_orientationPdf.StartInfo);
            }
            catch (Exception ex)
            {
                DiagnosticLogService.Log("Walk document open failed", ex);
                MessageBox.Show($"{ex.Message}\n\nExpected walk document: {OrientationPdfSession.TemplateName(_currentInspection) ?? "Template missing from INS"}.\n" +
                    "RED will not substitute another region's document. Contact Trent for the correct walk document.",
                    "Walk document unavailable", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally { _orientationPdfUiBusy = false; }
        }

        // Existing report navigation, close and updater hooks also guard general PDF sessions.
        private bool FinishOrientationEditing(bool leaving = true)
        {
            if (_finishingOrientationPdf || _pdfPolling) return false;
            _finishingOrientationPdf = true;
            try
            {
                PollPdfMonitors();
                foreach (var entry in _pdfSessions)
                {
                    if (!entry.Monitor.CanLeave(out string reason))
                    {
                        MessageBox.Show($"{reason}\n\nFinish/save/close the PDF in its editor, then use ATTACHMENTS > Retry Save if needed. " +
                            "RED cannot detect unsaved edits held only inside another app.\n\nWorking copy retained:\n" + entry.Monitor.WorkingPath,
                            "PDF changes still pending", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return false;
                    }
                }
                if (leaving)
                {
                    // Cleanup rechecks disk and can detect a late write/lock. Remove only sessions
                    // actually cleaned so a partial cleanup can safely resume the remaining monitors.
                    foreach (var entry in _pdfSessions.ToArray())
                    {
                        entry.Monitor.Cleanup();
                        _pdfSessions.Remove(entry);
                        if (ReferenceEquals(_orientationPdf, entry.Monitor)) _orientationPdf = null;
                    }
                    _pdfTimer?.Stop();
                    _orientationPdf = null;
                    UpdateOrientationPdfControls();
                }
                return true;
            }
            catch (Exception ex)
            {
                if (_pdfSessions.Count > 0) _pdfTimer?.Start();
                DiagnosticLogService.Log("PDF completion interrupted; remaining working copies retained", ex);
                MessageBox.Show(ex.Message + "\n\nPDF monitoring remains active. Finish/save/close the editor and use ATTACHMENTS > Retry Save.\n\nRetained working copies:\n" +
                    string.Join("\n", _pdfSessions.Select(entry => entry.Monitor.WorkingPath)),
                    "PDF changes still pending", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            finally { _finishingOrientationPdf = false; }
        }
    }
}
