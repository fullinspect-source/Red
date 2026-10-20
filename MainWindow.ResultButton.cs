using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using InspectionEditor.Services;

namespace InspectionEditor
{
    // Owner 2026-10-19 (fix list item 14): the result button at the top of the left panel. It always shows the current
    // result (Services\ResultLabel.cs) and opens RED's result picker, which writes StatusId, NextActionId and
    // NextActionText as before (SetResult, then the F-002 preserving save). Read-only reports (Outbox/Archive, the
    // locked BWT) show the result but do not open the picker. No confirmation popup.
    public partial class MainWindow
    {
        private bool _resultPickerPending;
        private bool _resultPlacementHooked;

        private void UpdateResultButton()
        {
            HideChangedFieldsNoticeForOtherReport();
            if (!HeaderSectionsAvailable)
            {
                ResultButton.Visibility = Visibility.Collapsed;
                UpdateSignButton();
                UpdateResultButtonPlacement();
                return;
            }
            var display = ResultLabel.For(
                HeaderRules.Int(Header("StatusId")),
                HeaderRules.Int(Header("NextActionId")),
                HeaderRules.StatusList(Header("StatusList")));
            ResultButton.Content = new TextBlock { Text = display.Text, TextTrimming = TextTrimming.CharacterEllipsis };
            ResultButton.Background = ResultBrush(display.Background);
            ResultButton.Foreground = ResultBrush(display.Foreground);
            ResultButton.BorderBrush = display.HasResult ? Brushes.Transparent : ResultBrush("#94A3B8");
            ResultButton.BorderThickness = new Thickness(display.HasResult ? 0 : 1);
            ResultButton.Cursor = _readOnlyMode ? Cursors.Arrow : Cursors.Hand;
            ResultButton.ToolTip = _readOnlyMode ? $"{display.Text}: the result of this read-only report" : $"{display.Text}: tap to set or change the result";
            AutomationProperties.SetName(ResultButton, display.Text);
            ResultButton.Visibility = Visibility.Visible;
            UpdateSignButton();
            UpdateResultButtonPlacement();
        }

        private static SolidColorBrush ResultBrush(string hex) => new((Color)ColorConverter.ConvertFromString(hex));

        private void ChecklistHeaderDock_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (e.WidthChanged)
                Dispatcher.BeginInvoke(new Action(UpdateResultButtonPlacement), DispatcherPriority.Loaded);
        }

        // The button (with Sign beside it, owner 2026-10-20) sits in the header row beside "Insp Items" when both fit
        // there; in a narrow left panel (classic mode's 350 px column) they move together to their own line just under the
        // row instead of being cut off.
        private StackPanel? _narrowResultRow;

        private void UpdateResultButtonPlacement()
        {
            if (!_resultPlacementHooked)
            {
                // The counter, star, trip badge and toggle change width after a report opens; decide again then.
                _resultPlacementHooked = true;
                foreach (var part in new FrameworkElement[] { InspectionTotalCounter, InspectionTotalStar, TripNumberBadge,
                             InlineEditorToggleButton, TopTextSizeUpButton, TopTextSizeDownButton })
                    part.SizeChanged += ChecklistHeaderDock_SizeChanged;
            }
            double available = ChecklistHeaderDock.ActualWidth;
            bool onNarrowLine = _narrowResultRow != null && ResultButton.Parent == _narrowResultRow;
            bool narrow = false;
            if (ResultButton.Visibility == Visibility.Visible && available > 0)
            {
                double used = 0;
                foreach (UIElement child in ChecklistHeaderDock.Children)
                {
                    if (child == ResultButton || child == SignButton || child == ChangedFieldsNotice || child == MegaStatsButton ||
                        child == DesignExtractionButton || child.Visibility == Visibility.Collapsed)
                        continue;
                    child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    used += child.DesiredSize.Width;
                }
                ResultButton.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                // DesiredSize includes the margin, which is 0 on the narrow line and 14 in the row.
                used += ResultButton.DesiredSize.Width + (onNarrowLine ? 14 : 0);
                if (SignButton.Visibility == Visibility.Visible)
                {
                    SignButton.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    used += SignButton.DesiredSize.Width; // its 8 px margin is the same in both places
                }
                narrow = used > available;
            }

            if (narrow && !onNarrowLine)
            {
                ChecklistHeaderDock.Children.Remove(ResultButton);
                ChecklistHeaderDock.Children.Remove(SignButton);
                ResultButton.Margin = new Thickness(0);
                _narrowResultRow ??= new StackPanel { Orientation = Orientation.Horizontal };
                _narrowResultRow.Children.Add(ResultButton);
                _narrowResultRow.Children.Add(SignButton);
                ResultButtonNarrowHost.Child = _narrowResultRow;
            }
            else if (!narrow && onNarrowLine)
            {
                _narrowResultRow!.Children.Clear();
                ResultButtonNarrowHost.Child = null;
                ResultButton.Margin = new Thickness(14, 0, 0, 0);
                int index = ChecklistHeaderDock.Children.IndexOf(TopTextSizeDownButton) + 1;
                ChecklistHeaderDock.Children.Insert(index, ResultButton);
                ChecklistHeaderDock.Children.Insert(index + 1, SignButton);
            }
            ResultButtonNarrowHost.Visibility = _narrowResultRow != null && ResultButton.Parent == _narrowResultRow && ResultButton.Visibility == Visibility.Visible
                ? Visibility.Visible : Visibility.Collapsed;
        }

        private void ResultButton_Click(object sender, RoutedEventArgs e)
        {
            if (_readOnlyMode || !HeaderSectionsAvailable || _isLoadingFile || _resultPickerPending)
                return;
            var report = _currentInspection;
            bool pausedBefore = _diskConflictPending;
            _resultPickerPending = true;
            AutoApplyCurrentItem();
            // That save may have stopped on a changed-on-disk file and queued a Reload or Close (Background priority):
            // let it run first, then open the picker only for this same report.
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (_readOnlyMode || !HeaderSectionsAvailable || _isLoadingFile || !ReferenceEquals(_currentInspection, report) ||
                        // Saving was already paused after a changed-on-disk stop: offer the choices first, as Save does.
                        // (When the save above just stopped, the person has already answered them.)
                        (_diskConflictPending && (!pausedBefore || !TrySaveCurrentInspection())) ||
                        !ReferenceEquals(_currentInspection, report))
                    {
                        UpdateResultButton();
                        return;
                    }
                    ShowResultPicker(closeAfterPicker: false, changeOnly: true);
                    // The End section's Status and Next Action rows, the markers and the button show the new result.
                    PopulateTreeView(SearchFilterBox.Text);
                    UpdateResultButton();
                }
                finally { _resultPickerPending = false; }
            }), DispatcherPriority.Background);
        }
    }
}
