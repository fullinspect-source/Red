using InspectionEditor.Services;
using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace InspectionEditor
{
    public partial class MainWindow
    {
        private OrientationPdfSession? _orientationPdf;
        private bool _finishingOrientationPdf;
        private bool _orientationPdfUiBusy;
        private string OrientationWorkingRoot => Path.Combine(AppIdentity.LocalAppDataPath, "OrientationPdf");

        private void UpdateOrientationPdfControls()
        {
            bool applicable = OrientationPdfSession.IsApplicable(_currentInspection);
            OrientationPdfPanel.Visibility = applicable ? Visibility.Visible : Visibility.Collapsed;
            OpenOrientationPdfButton.IsEnabled = applicable && !_readOnlyMode;
            SaveOrientationPdfButton.IsEnabled = applicable && !_readOnlyMode && _orientationPdf != null;
            OrientationPdfHint.Text = _orientationPdf == null
                ? "Open the saved Orientation PDF, or prefill the exact INS template with this report's current values."
                : "Save and close the PDF editor, then tap Save PDF to INS. Leaving the report also lets you discard this PDF session.";
            OrientationPdfHint.ToolTip = _orientationPdf?.WorkingPath;
        }

        // Returning false cancels without selecting or replacing any attachment.
        private bool SelectOrientationAttachment(out int? index)
        {
            index = null;
            if (_currentInspection == null) return false;
            var candidates = OrientationPdfSession.FindCandidates(_currentInspection);
            if (candidates.Count == 1) index = candidates[0].Index;
            if (candidates.Count > 1)
            {
                var list = new ComboBox { ItemsSource = candidates, DisplayMemberPath = "Filename", Margin = new Thickness(8), MinWidth = 300 };
                var open = new Button { Content = "Use selected PDF", Margin = new Thickness(8), Padding = new Thickness(12, 8, 12, 8), IsDefault = true };
                var panel = new StackPanel();
                panel.Children.Add(new TextBlock { Text = "More than one Orientation PDF is embedded. Select the intended document.", Margin = new Thickness(8), TextWrapping = TextWrapping.Wrap });
                panel.Children.Add(list); panel.Children.Add(open);
                var window = new Window { Title = "Select Orientation PDF", Owner = this, Content = panel,
                    Width = 440, SizeToContent = SizeToContent.Height, WindowStartupLocation = WindowStartupLocation.CenterOwner };
                open.Click += (_, _) => { if (list.SelectedItem != null) window.DialogResult = true; };
                if (window.ShowDialog() != true || list.SelectedItem is not OrientationPdfSession.Candidate selected) return false;
                index = selected.Index;
            }
            return true;
        }

        private void OpenOrientationPdfButton_Click(object sender, RoutedEventArgs e)
        {
            if (_readOnlyMode || _currentInspection == null || _currentFilePath == null) return;
            if (_orientationPdfUiBusy) return;
            _orientationPdfUiBusy = true;
            try
            {
                if (_orientationPdf == null)
                {
                    SyncCurrentItemFromUI();
                    if (!SelectOrientationAttachment(out int? index)) return;
                    if (index.HasValue)
                        _orientationPdf = OrientationPdfSession.OpenEmbedded(_currentInspection, _currentFilePath, index.Value, OrientationWorkingRoot);
                    else
                        _orientationPdf = OrientationPdfSession.OpenTemplate(_currentInspection, _currentFilePath, OrientationWorkingRoot);
                }
                UpdateOrientationPdfControls();
                // Shell association decides the viewer/editor. Its lifetime is NOT a reliable save signal.
                using var process = Process.Start(_orientationPdf.StartInfo);
            }
            catch (Exception ex) { ShowOrientationError(ex); }
            finally { _orientationPdfUiBusy = false; }
        }

        private void SaveOrientationPdfButton_Click(object sender, RoutedEventArgs e)
        {
            if (FinishOrientationEditing(leaving: false)) UpdateOrientationPdfControls();
        }

        private bool FinishOrientationEditing(bool leaving = true)
        {
            if (_orientationPdf == null) return true;
            if (_finishingOrientationPdf) return false;
            _finishingOrientationPdf = true;
            try
            {
                string guidance = "Save and close the PDF in your external editor before choosing Yes. RED cannot detect unsaved edits inside another app.\n\n";
                var result = MessageBox.Show(guidance + (leaving
                    ? "Yes: Save this working PDF into the INS and continue.\nNo: Don't save this PDF session and continue.\nCancel: Stay in this report. Other report edits keep their normal save safeguards."
                    : "Have you saved and closed the editor? Yes saves this working PDF into the INS. No cancels this save and keeps the PDF session open.") +
                    "\n\n" + _orientationPdf.WorkingPath, "Save Orientation PDF to INS",
                    leaving ? MessageBoxButton.YesNoCancel : MessageBoxButton.YesNo, MessageBoxImage.Question,
                    leaving ? MessageBoxResult.Cancel : MessageBoxResult.No);
                var decision = result == MessageBoxResult.Yes ? OrientationPdfDecision.Save
                    : leaving && result == MessageBoxResult.No ? OrientationPdfDecision.Discard : OrientationPdfDecision.Cancel;
                if (!_orientationPdf.TryFinish(decision, TrySaveCurrentInspection)) return false;
                _orientationPdf = null;
                UpdateOrientationPdfControls();
                return true;
            }
            catch (Exception ex) { ShowOrientationError(ex); return false; }
            finally { _finishingOrientationPdf = false; }
        }

        private void ShowOrientationError(Exception ex)
        {
            DiagnosticLogService.Log("Orientation PDF retained after failure", ex);
            UpdateOrientationPdfControls();
            MessageBox.Show($"Orientation PDF could not be completed. No unrelated attachment was replaced.\n\n{ex.Message}\n\n" +
                (_orientationPdf == null ? "Your original PDF is unchanged." : $"Working copy retained at:\n{_orientationPdf.WorkingPath}"),
                "Orientation PDF", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
