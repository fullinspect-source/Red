using InspectionEditor.Services;
using Microsoft.Win32;
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
            ImportOrientationPdfButton.IsEnabled = applicable && !_readOnlyMode;
            SaveOrientationPdfButton.IsEnabled = applicable && !_readOnlyMode && _orientationPdf != null;
            OrientationPdfHint.Text = _orientationPdf == null
                ? "Open the embedded PDF or the exact INS template. Import a PDF if the template is unavailable."
                : "Save and close the PDF editor, then tap Save PDF to INS. If you used Save As, import that saved PDF.";
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

        private string? PickOrientationPdf()
        {
            var picker = new OpenFileDialog { Title = "Select the correct Orientation PDF (or your edited Save As copy)",
                Filter = "PDF documents (*.pdf)|*.pdf", CheckFileExists = true, Multiselect = false };
            return picker.ShowDialog(this) == true ? picker.FileName : null;
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
                    if (!SelectOrientationAttachment(out int? index)) return;
                    if (index.HasValue)
                        _orientationPdf = OrientationPdfSession.OpenEmbedded(_currentInspection, _currentFilePath, index.Value, OrientationWorkingRoot);
                    else
                    {
                        string? source = OrientationPdfSession.FindTemplate(_currentInspection, _currentFilePath);
                        if (source == null)
                        {
                            MessageBox.Show("No Orientation PDF is embedded and the INS template is not available in the inspection Documents folder.\n\n" +
                                $"Template: {OrientationPdfSession.TemplateName(_currentInspection) ?? "not specified"}\n\n" +
                                "Select/import the correct PDF. RED does not manufacture the builder's form.",
                                "Select Orientation PDF", MessageBoxButton.OK, MessageBoxImage.Information);
                            source = PickOrientationPdf();
                            if (source == null) return;
                        }
                        _orientationPdf = OrientationPdfSession.Import(_currentInspection, _currentFilePath, source, OrientationWorkingRoot);
                        if (_orientationPdf.Capture(_currentInspection)) MarkUnsaved();
                    }
                }
                UpdateOrientationPdfControls();
                // Shell association decides the viewer/editor. Its lifetime is NOT a reliable save signal.
                using var process = Process.Start(_orientationPdf.StartInfo);
            }
            catch (Exception ex) { ShowOrientationError(ex); }
            finally { _orientationPdfUiBusy = false; }
        }

        private void ImportOrientationPdfButton_Click(object sender, RoutedEventArgs e)
        {
            if (_readOnlyMode || _currentInspection == null || _currentFilePath == null) return;
            if (_orientationPdfUiBusy) return;
            _orientationPdfUiBusy = true;
            try
            {
                if (_orientationPdf != null)
                {
                    if (MessageBox.Show("Save and close the PDF editor first, then select your edited PDF (including a Save As copy).\n\n" +
                        "Replace this session's working copy? Your selected original file will not be changed.",
                        "Import edited Orientation PDF", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                    string? edited = PickOrientationPdf();
                    if (edited == null) return;
                    _orientationPdf.ReplaceWorkingCopy(edited);
                    if (_orientationPdf.Capture(_currentInspection)) MarkUnsaved();
                    UpdateOrientationPdfControls();
                    return;
                }
                if (!SelectOrientationAttachment(out int? index)) return;
                string? source = PickOrientationPdf();
                if (source == null) return;
                if (index.HasValue && MessageBox.Show("Replace only the selected Orientation PDF with this file? Other attachments and metadata will be preserved.",
                    "Import Orientation PDF", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                _orientationPdf = OrientationPdfSession.Import(_currentInspection, _currentFilePath, source, OrientationWorkingRoot, index);
                if (_orientationPdf.Capture(_currentInspection)) MarkUnsaved();
                UpdateOrientationPdfControls();
                using var process = Process.Start(_orientationPdf.StartInfo);
            }
            catch (Exception ex) { ShowOrientationError(ex); }
            finally { _orientationPdfUiBusy = false; }
        }

        private void SaveOrientationPdfButton_Click(object sender, RoutedEventArgs e)
        {
            if (FinishOrientationEditing()) UpdateOrientationPdfControls();
        }

        private bool FinishOrientationEditing()
        {
            if (_orientationPdf == null) return true;
            if (_finishingOrientationPdf) return false;
            _finishingOrientationPdf = true;
            try
            {
                if (MessageBox.Show("Save and close the PDF in your external editor first. RED cannot detect unsaved edits inside another app.\n\n" +
                    "If you used Save As, choose No and use Import PDF to select your edited file.\n\n" +
                    "Have you saved and closed the editor? Yes saves this working PDF into the INS; No keeps the report open.\n\n" + _orientationPdf.WorkingPath,
                    "Save Orientation PDF to INS", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return false;
                if (!TrySaveCurrentInspection()) return false;
                _orientationPdf.Complete();
                _orientationPdf = null;
                UpdateOrientationPdfControls();
                return true;
            }
            catch (Exception ex) { MarkUnsaved(); ShowOrientationError(ex); return false; }
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
