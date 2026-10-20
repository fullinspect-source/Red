using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using InspectionEditor.Models;
using InspectionEditor.Services;

namespace InspectionEditor
{
    // Owner 2026-10-19 (fix list p3): no "please confirm" popups. When RED itself changes a value for the inspector,
    // each changed value field flashes red 5 times (ValueFlash) and the change stands. The editor rebuilds rows after a
    // change (PopulateTreeView, RefreshInlineItemRow), so the flash starts once the rebuilt controls are on screen. A
    // changed value with nothing on screen to flash (a search filter or a collapsed section hides it) is named in a
    // short note in the left panel's header row instead.
    public partial class MainWindow
    {
        private DispatcherTimer? _changedNoticeTimer;

        private void FlashChangedValues(params Item?[] items)
        {
            var changed = items.Where(item => item != null).Select(item => item!).Distinct().ToList();
            if (changed.Count == 0)
                return;
            Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
            {
                var unseen = new List<string>();
                foreach (var item in changed)
                {
                    int seen = 0;
                    foreach (var (target, keepGoing) in ValueFlashTargets(item).GroupBy(t => t.Element).Select(g => g.First()))
                        if (target.IsVisible && ValueFlash.Start(target, keepGoing: keepGoing) && InScrolledView(target))
                            seen++;
                    if (seen == 0)
                        unseen.Add($"{item.Number} {item.Name}".Trim());
                }
                ShowChangedFieldsNotice(unseen);
            }));
        }

        // A row scrolled out of its list still counts as visible to WPF; it is seen only inside every scroll viewport.
        private static bool InScrolledView(FrameworkElement target)
        {
            if (target.ActualWidth <= 0 || target.ActualHeight <= 0)
                return false;
            for (var parent = VisualTreeHelper.GetParent(target); parent != null; parent = VisualTreeHelper.GetParent(parent))
            {
                if (parent is not ScrollViewer viewer)
                    continue;
                try
                {
                    var bounds = target.TransformToAncestor(viewer).TransformBounds(new Rect(target.RenderSize));
                    if (!bounds.IntersectsWith(new Rect(0, 0, viewer.ActualWidth, viewer.ActualHeight)))
                        return false;
                }
                catch (InvalidOperationException)
                {
                    return false;
                }
            }
            return true;
        }

        private InspectionFile? _changedNoticeReport;

        // The note belongs to the report it was shown for: it goes when that report closes or another one opens.
        private void HideChangedFieldsNoticeForOtherReport()
        {
            if (ChangedFieldsNotice.Visibility != Visibility.Visible || ReferenceEquals(_currentInspection, _changedNoticeReport))
                return;
            _changedNoticeTimer?.Stop();
            ChangedFieldsNotice.Visibility = Visibility.Collapsed;
        }

        private void ShowChangedFieldsNotice(IReadOnlyCollection<string> labels)
        {
            if (labels.Count == 0 || _currentInspection == null)
                return;
            _changedNoticeReport = _currentInspection;
            ChangedFieldsNotice.Text = "✓ Also changed: " + string.Join(", ", labels);
            ChangedFieldsNotice.ToolTip = ChangedFieldsNotice.Text;
            ChangedFieldsNotice.Visibility = Visibility.Visible;
            _changedNoticeTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
            _changedNoticeTimer.Stop();
            _changedNoticeTimer.Tick -= ChangedNoticeTimer_Tick;
            _changedNoticeTimer.Tick += ChangedNoticeTimer_Tick;
            _changedNoticeTimer.Start();
        }

        private void ChangedNoticeTimer_Tick(object? sender, EventArgs e)
        {
            _changedNoticeTimer?.Stop();
            ChangedFieldsNotice.Visibility = Visibility.Collapsed;
        }

        // The controls that show the item's value: the inline value box or combo, else the button of the value now
        // chosen (Pass/Fail, Yes/No, NI), else the inline row; grid cells; the classic pane's value box while the item
        // is the current one; and the classic tree node.
        private IEnumerable<(FrameworkElement Element, Func<bool>? KeepGoing)> ValueFlashTargets(Item item)
        {
            if (_inlineGridCellBoxes.TryGetValue(item, out var inlineCell) && inlineCell.IsLoaded)
                yield return (inlineCell, null);
            if (_paneGridCellBoxes.TryGetValue(item, out var paneCell) && paneCell.IsLoaded)
                yield return (paneCell, null);
            if (_inlineItemRows.TryGetValue(item, out var entry) && entry.Row.IsLoaded)
            {
                var controls = ValueFlashDescendants(entry.Row).ToList();
                var targets = controls.Where(c => c.GetValue(InlineValueDisplayProperty) is true).Cast<FrameworkElement>().ToList();
                string value = item.Value?.ToString() ?? "";
                targets.AddRange(controls.OfType<ButtonBase>().Where(button => button.Tag is InlineValueAction action &&
                    ReferenceEquals(action.Item, item) && action.Value.Equals(value, StringComparison.OrdinalIgnoreCase)));
                if (targets.Count == 0)
                    targets.Add(entry.Row);
                foreach (var target in targets)
                    yield return (target, null);
            }
            // The pane's value box is shared by every item: the flash stops when another item is shown in it.
            if (ReferenceEquals(item, _currentItem) && StatusTextBox.IsVisible)
                yield return (StatusTextBox, () => ReferenceEquals(_currentItem, item));
            foreach (var sectionNode in SectionsTreeView.Items.OfType<TreeViewItem>())
                foreach (var itemNode in sectionNode.Items.OfType<TreeViewItem>())
                    if (ReferenceEquals(itemNode.Tag, item) && itemNode.Header is Panel header && header.IsLoaded)
                        yield return (header, null);
        }

        private static IEnumerable<Control> ValueFlashDescendants(DependencyObject root)
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is Control control)
                    yield return control;
                // A value combo's inner text box is part of the combo, not another value field.
                if (child is ComboBox)
                    continue;
                foreach (var nested in ValueFlashDescendants(child))
                    yield return nested;
            }
        }
    }
}
