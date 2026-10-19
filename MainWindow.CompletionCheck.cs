using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using InspectionEditor.Models;
using InspectionEditor.Services;

namespace InspectionEditor
{
    // F-012 completion check: runs silently after every save (W1) and on open, drives the BWT read-only lock (§4b), and
    // gates Submit, Approve and Print (W2-W3) with INSPECT's messages plus RED's problem list (W4).
    public partial class MainWindow
    {
        private CompletionResult? _lastValidation;
        private Button? _completionCheckButton;

        private CompletionResult RunCompletionCheck()
        {
            _lastValidation = _currentInspection == null || !_saveService.HasFile
                ? new CompletionResult()
                : InspectionValidator.Validate(_currentInspection, Header);
            UpdateCompletionCheckButton();
            return _lastValidation;
        }

        // Called by F-013 Submit, F-018 Approve and F-022 Print. False (and the problem list) when invalid.
        internal bool PassesCompletionGate(string refusedMessage, string title)
        {
            var result = RunCompletionCheck();
            if (result.IsValid)
                return true;
            MessageBox.Show(this, refusedMessage, title, MessageBoxButton.OK, MessageBoxImage.Warning);
            ShowProblemList(result);
            return false;
        }

        private void AfterOpenCompletionCheck()
        {
            var result = RunCompletionCheck();
            ApplyBwtLock(result);
        }

        private void AfterSaveCompletionCheck()
        {
            if (_currentInspection == null || !_saveService.HasFile || _isLoadingFile)
            {
                UpdateCompletionCheckButton();
                return;
            }
            var result = RunCompletionCheck();
            ApplyBwtLock(result);
        }

        // §4b L1-L3: a valid, signed BWT becomes read-only (no unlock in v1, owner Q-19); Submit stays available (L2).
        private void ApplyBwtLock(CompletionResult result)
        {
            if (_currentInspection == null || _readOnlyMode)
                return;
            if (!InspectionValidator.IsBwtLocked(_currentInspection, Header, !string.IsNullOrEmpty(_currentFilePath), result))
                return;
            _readOnlyMode = true;
            Title = $"RED {AppIdentity.VersionDisplay} - {_currentInspection.InspectionCode} [READ-ONLY]";
            SaveFileButton.Opacity = 0.4;
            UpdateOrientationPdfControls();
            PopulateTreeView(SearchFilterBox.Text);
            if (_editorLoadedItem != null)
                LoadStatusControls(_editorLoadedItem);
            ApplyReadOnlyUi();
        }

        private void InstallCompletionCheckButton()
        {
            if (SaveFileButton.Parent is not Panel panel || _completionCheckButton != null)
                return;
            _completionCheckButton = new Button
            {
                Content = "✔ Check",
                Background = new SolidColorBrush(Color.FromRgb(71, 85, 105)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                IsEnabled = false,
                ToolTip = "Run INSPECT's completion check and list what still blocks Submit"
            };
            _completionCheckButton.Click += (_, _) =>
            {
                var result = RunCompletionCheck();
                if (result.IsValid && !result.Problems.Any() && !result.FeeRuleSkipped)
                    MessageBox.Show(this, "No problems: the inspection passes the completion check.", "Completion check",
                        MessageBoxButton.OK, MessageBoxImage.Information);
                else
                    ShowProblemList(result);
            };
            panel.Children.Insert(panel.Children.IndexOf(SaveFileButton) + 1, _completionCheckButton);
        }

        private void UpdateCompletionCheckButton()
        {
            UpdateSubmitButton(); // F-013
            if (_completionCheckButton == null)
                return;
            _completionCheckButton.IsEnabled = _currentInspection != null && _saveService.HasFile;
            int blocking = _lastValidation?.Blocking.Count() ?? 0;
            _completionCheckButton.Content = blocking == 0 ? "✔ Check" : $"✔ Check ({blocking})";
        }

        // W4: the problem list with a jump to the field or item (RED addition; it changes nothing that is blocked).
        private void ShowProblemList(CompletionResult result)
        {
            var list = new ListBox { MinWidth = 520, MinHeight = 220, MaxHeight = 520 };
            foreach (var problem in result.Problems.OrderBy(p => p.Blocking ? 0 : 1))
            {
                list.Items.Add(new ListBoxItem
                {
                    Content = problem.Blocking ? problem.Text : $"{problem.Text} (does not block)",
                    Tag = problem,
                    Foreground = problem.Blocking ? new SolidColorBrush(Color.FromRgb(153, 27, 27)) : new SolidColorBrush(Color.FromRgb(100, 116, 139))
                });
            }
            if (result.FeeRuleSkipped)
                list.Items.Add(new ListBoxItem { Content = InspectionValidator.FeeRuleSkippedText, Foreground = Brushes.Gray });

            var window = new Window
            {
                Title = result.IsValid ? "Completion check: nothing blocks Submit" : $"Completion check: {result.Blocking.Count()} problem(s)",
                Owner = this,
                SizeToContent = SizeToContent.WidthAndHeight,
                WindowStartupLocation = WindowStartupLocation.CenterOwner
            };
            var go = new Button { Content = "Go to", Padding = new Thickness(14, 4, 14, 4), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
            var close = new Button { Content = "Close", Padding = new Thickness(14, 4, 14, 4), IsCancel = true };
            void Jump()
            {
                if (list.SelectedItem is ListBoxItem { Tag: CompletionProblem problem })
                {
                    window.Close();
                    JumpToProblem(problem);
                }
            }
            go.Click += (_, _) => Jump();
            list.MouseDoubleClick += (_, _) => Jump();
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
            buttons.Children.Add(go);
            buttons.Children.Add(close);
            var root = new StackPanel { Margin = new Thickness(12) };
            root.Children.Add(new TextBlock { Text = "Double-click a problem to go to it.", Margin = new Thickness(0, 0, 0, 6) });
            root.Children.Add(list);
            root.Children.Add(buttons);
            window.Content = root;
            window.ShowDialog();
        }

        private void JumpToProblem(CompletionProblem problem)
        {
            if (problem.Item != null && EditorEditService.Owns(_currentInspection, problem.Item))
            {
                AutoApplyCurrentItem();
                LoadItemEditor(problem.Item);
                SelectItemInTreeView(problem.Item);
                if (_inlineEditorMode)
                    KeepInlineItemHeaderVisible(problem.Item);
                return;
            }
            if (problem.HeaderKey != null)
            {
                bool end = problem.HeaderKey is "StatusId" or "NextActionId" or "NextActionText" or "InternalNotes" or "Signature"
                           or "OtherFeeDescription1" or "OtherFeeDescription2";
                if (_inlineEditorMode)
                {
                    string sectionKey = end ? EndSectionKey : TopSectionKey;
                    _expandedHeaderSections.Add(sectionKey);
                    PopulateInlineChecklist(SearchFilterBox.Text);
                    // Hands-on pass 2026-10-19: End sits below every template section, so expanding it alone showed no
                    // change. Scroll the field's row (else the section header) into view.
                    string headerKey = problem.HeaderKey;
                    Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() =>
                    {
                        FrameworkElement? target = _headerMarkerBlocks.TryGetValue(headerKey, out var marker)
                            ? marker.Parent as FrameworkElement ?? marker
                            : null;
                        target ??= InlineChecklistPanel.Children.OfType<Border>().FirstOrDefault(b => Equals(b.Tag, sectionKey));
                        target?.BringIntoView();
                    }));
                }
                else
                {
                    OpenHeaderSectionWindow(top: !end);
                }
            }
        }

        // W5: RED's INC filter shows exactly the items INSPECT's item rules reject; REQ shows the items the rules apply to.
        private bool IsIncompleteByCompletionRules(Item item) =>
            _currentInspection != null && InspectionValidator.ItemProblem(item, _currentInspection) != null;

        private static bool IsCheckedByCompletionRules(Item item) => item.Required && !InspectionValidator.IsHidden(item);
    }
}
