using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using InspectionEditor.Models;
using InspectionEditor.Services;

namespace InspectionEditor
{
    // F-005 item-group grids. Layout and value rules live in Services\ItemGroupLayout.cs; this file draws them in the
    // inline checklist (left), in the classic tree's right pane, and gives the right pane a cell editor.
    public partial class MainWindow
    {
        private readonly Dictionary<Item, TextBox> _inlineGridCellBoxes = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<Item, TextBox> _paneGridCellBoxes = new(ReferenceEqualityComparer.Instance);
        private bool _itemGroupCellRefreshing;

        private static readonly Brush GridPassBrush = new SolidColorBrush(Color.FromRgb(22, 128, 61));
        private static readonly Brush GridPassBackground = new SolidColorBrush(Color.FromRgb(220, 252, 231));
        private static readonly Brush GridFailBrush = new SolidColorBrush(Color.FromRgb(185, 28, 28));
        private static readonly Brush GridFailBackground = new SolidColorBrush(Color.FromRgb(254, 226, 226));
        private static readonly Brush GridCellBorder = new SolidColorBrush(Color.FromRgb(148, 163, 184));
        private static readonly Brush GridMissingBorder = new SolidColorBrush(Color.FromRgb(220, 38, 38));

        private bool AnyInlineChecklistFilterActive(string? filter) =>
            !string.IsNullOrWhiteSpace(filter) || _ofiFilterActive || _reqFilterActive || _incFilterActive;

        // Items of one section the inline checklist lists, in section order. A drawn grid is all-or-nothing: shown
        // without filters (Hide*Button flags never hide a grid), and with a filter when any of its cells matches.
        private List<Item> GetInlineSectionVisibleItems(Section section, string? filter, bool isSCI)
        {
            var runs = ItemGroupLayout.Detect(section).Where(run => run.Recognized).ToList();
            var gridVisible = new Dictionary<ItemGroupRun, bool>();
            var result = new List<Item>();
            foreach (var item in section.Items)
            {
                var run = runs.FirstOrDefault(candidate => candidate.CellFor(item) != null);
                if (run == null)
                {
                    if (ShouldShowInlineItem(item, filter, isSCI))
                        result.Add(item);
                    continue;
                }

                if (!gridVisible.TryGetValue(run, out bool visible))
                {
                    visible = !AnyInlineChecklistFilterActive(filter) ||
                              run.Items.Any(cellItem => GridCellMatchesFilter(cellItem, filter));
                    gridVisible[run] = visible;
                }
                if (visible)
                    result.Add(item);
            }
            return result;
        }

        private bool GridCellMatchesFilter(Item item, string? filter)
        {
            if (!string.IsNullOrWhiteSpace(filter))
            {
                string filterLower = filter.ToLowerInvariant();
                bool matches = item.Name?.ToLowerInvariant().Contains(filterLower) == true ||
                               item.Number?.ToLowerInvariant().Contains(filterLower) == true ||
                               item.Comments?.ToLowerInvariant().Contains(filterLower) == true ||
                               item.Value?.ToString()?.ToLowerInvariant().Contains(filterLower) == true;
                if (!matches)
                    return false;
            }
            if (_ofiFilterActive && !string.Equals(item.Value?.ToString(), "Fail", StringComparison.OrdinalIgnoreCase))
                return false;
            if (_reqFilterActive && !IsCheckedByCompletionRules(item))
                return false;
            if (_incFilterActive && !IsIncompleteByCompletionRules(item))
                return false;
            return true;
        }

        private void ClearInlineItemGroupCells() => _inlineGridCellBoxes.Clear();

        // Adds a section's rows to the inline checklist: one grid per drawn run, the notice before a known group whose
        // layout is not INSPECT's (G3), and normal rows for everything else.
        private void AddInlineSectionItems(Section section, List<Item> visibleItems)
        {
            var runs = ItemGroupLayout.Detect(section);
            var drawn = new HashSet<ItemGroupRun>();
            foreach (var item in visibleItems)
            {
                var run = runs.FirstOrDefault(candidate => candidate.Items.Contains(item, ReferenceEqualityComparer.Instance));
                if (run is { Recognized: true })
                {
                    if (drawn.Add(run))
                        InlineChecklistPanel.Children.Add(CreateItemGroupGrid(section, run, inPane: false));
                    continue;
                }
                if (run is { Recognized: false, Problem: not null } && drawn.Add(run))
                    InlineChecklistPanel.Children.Add(CreateItemGroupNotice(run));
                InlineChecklistPanel.Children.Add(CreateInlineItemRow(section, item));
            }
        }

        private Border CreateItemGroupNotice(ItemGroupRun run)
        {
            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(254, 243, 199)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(217, 119, 6)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 2, 0, 4),
                ToolTip = $"{run.GroupControlName}: {run.Problem}. The items are shown as normal rows.",
                Child = new TextBlock
                {
                    Text = ItemGroupLayout.NotRecognisedText,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(Color.FromRgb(146, 64, 14)),
                    FontSize = Math.Max(11, _checklistFontSize - 1)
                }
            };
            StretchInlineWidth(border);
            return border;
        }

        private Border CreateItemGroupGrid(Section section, ItemGroupRun run, bool inPane)
        {
            var cellBoxes = inPane ? _paneGridCellBoxes : _inlineGridCellBoxes;
            double fontSize = inPane ? 12 : Math.Max(11, _checklistFontSize - 1);
            var stack = new StackPanel();
            stack.Children.Add(new TextBlock
            {
                Text = $"{run.Title}  ({run.Items.Count} items)",
                FontWeight = FontWeights.Bold,
                FontSize = fontSize + 1,
                Foreground = new SolidColorBrush(Color.FromRgb(30, 41, 59)),
                Margin = new Thickness(0, 0, 0, 6)
            });

            var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            for (int c = 0; c < run.ColumnCount; c++)
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(inPane ? 76 : 104) });
            for (int r = 0; r <= run.RowCount; r++)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            for (int c = 0; c < run.ColumnCount; c++)
            {
                var header = new TextBlock
                {
                    Text = run.ColumnHeaders[c],
                    FontWeight = FontWeights.SemiBold,
                    FontSize = fontSize,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center,
                    Foreground = new SolidColorBrush(Color.FromRgb(71, 85, 105)),
                    Margin = new Thickness(2, 0, 2, 4),
                    VerticalAlignment = VerticalAlignment.Bottom
                };
                Grid.SetRow(header, 0);
                Grid.SetColumn(header, c + 1);
                grid.Children.Add(header);
            }

            for (int r = 0; r < run.RowCount; r++)
            {
                var label = new TextBlock
                {
                    Text = run.RowLabels[r],
                    FontSize = fontSize,
                    Foreground = new SolidColorBrush(Color.FromRgb(51, 65, 85)),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 8, 0)
                };
                Grid.SetRow(label, r + 1);
                Grid.SetColumn(label, 0);
                grid.Children.Add(label);

                for (int c = 0; c < run.ColumnCount; c++)
                {
                    var cell = run.CellAt(r, c);
                    if (cell == null)
                        continue;
                    var host = CreateItemGroupCellHost(section, run, cell, inPane, fontSize, cellBoxes);
                    Grid.SetRow(host, r + 1);
                    Grid.SetColumn(host, c + 1);
                    grid.Children.Add(host);
                }
            }

            stack.Children.Add(grid);
            if (run.Kind == ItemGroupKind.TruckPlacement)
                stack.Children.Add(CreateItemGroupHint("Arrival time: type 8:25 or 20:25 (24-hour display). Number cells: type a value or use the number pad on the right."));
            else if (run.Kind == ItemGroupKind.DrainageVerification)
                stack.Children.Add(CreateItemGroupHint("Result is calculated: Pass when Expected and Shot Elevation differ by 0.05 or less."));

            var border = new Border
            {
                Tag = run,
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(203, 213, 225)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 2, 0, 6),
                Child = inPane ? stack : new ScrollViewer
                {
                    Content = stack,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Disabled
                }
            };
            if (!inPane)
                StretchInlineWidth(border);
            return border;
        }

        private TextBlock CreateItemGroupHint(string text) => new()
        {
            Text = text,
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0)
        };

        private FrameworkElement CreateItemGroupCellHost(Section section, ItemGroupRun run, ItemGroupCell cell, bool inPane,
            double fontSize, Dictionary<Item, TextBox> cellBoxes)
        {
            var box = new TextBox
            {
                Tag = cell,
                Text = ItemGroupLayout.DisplayValue(cell),
                FontSize = fontSize,
                Padding = new Thickness(4, 3, 4, 3),
                Margin = new Thickness(2),
                MinHeight = 28,
                HorizontalContentAlignment = cell.Kind == ItemGroupCellKind.Number ? HorizontalAlignment.Right : HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                IsReadOnly = _readOnlyMode || cell.Kind == ItemGroupCellKind.Result,
                IsTabStop = cell.Kind != ItemGroupCellKind.Result
            };
            if (cell.Kind == ItemGroupCellKind.Number)
                InputMethod.SetPreferredImeState(box, InputMethodState.Off);
            cellBoxes[cell.Item] = box;
            ApplyItemGroupCellVisual(section, cell, box);

            box.GotKeyboardFocus += (_, _) => ItemGroupCell_GotFocus(cell, box, inPane);
            box.LostKeyboardFocus += (_, _) => CommitItemGroupCell(cell, box);
            box.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter)
                {
                    CommitItemGroupCell(cell, box);
                    box.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
                    e.Handled = true;
                }
                else if (e.Key == Key.Escape)
                {
                    box.Text = ItemGroupLayout.DisplayValue(cell);
                    box.SelectAll();
                    e.Handled = true;
                }
            };

            var host = new Grid();
            host.Children.Add(box);
            if (cell.Item.Required)
            {
                host.Children.Add(new TextBlock
                {
                    Text = "*",
                    Foreground = GridMissingBorder,
                    FontWeight = FontWeights.Bold,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(4, 1, 0, 0),
                    IsHitTestVisible = false
                });
            }
            return host;
        }

        private void ApplyItemGroupCellVisual(Section section, ItemGroupCell cell, TextBox box)
        {
            var item = cell.Item;
            var tips = new List<string> { $"Item {item.Number}: {item.Name}" };
            box.Background = Brushes.White;
            box.Foreground = new SolidColorBrush(Color.FromRgb(15, 23, 42));
            box.FontWeight = string.IsNullOrWhiteSpace(item.Value?.ToString()) ? FontWeights.Normal : FontWeights.SemiBold;
            box.BorderBrush = GridCellBorder;
            box.BorderThickness = new Thickness(1);

            if (cell.Kind == ItemGroupCellKind.Result)
            {
                string value = item.Value?.ToString() ?? "";
                if (value == "Pass") { box.Foreground = GridPassBrush; box.Background = GridPassBackground; box.FontWeight = FontWeights.Bold; }
                else if (value == "Fail") { box.Foreground = GridFailBrush; box.Background = GridFailBackground; box.FontWeight = FontWeights.Bold; }
                tips.Add("Calculated: Pass when |Expected Elevation - Shot Elevation| <= 0.05, otherwise Fail.");
            }
            else if (cell.Kind == ItemGroupCellKind.Time)
            {
                tips.Add($"Stored as {(string.IsNullOrEmpty(item.Value?.ToString()) ? "blank" : item.Value)}; shown as HH:mm.");
            }
            else
            {
                // CPP aids keep working on the same items: show the slab-engineering / EC guidance on the cell.
                var assist = GetInlineDesignAssist(section, item);
                if (assist != null)
                {
                    tips.Add(assist.Text);
                    if (assist.State == EnergyComplianceService.BannerState.Green)
                        box.Background = new SolidColorBrush(Color.FromRgb(236, 252, 203));
                    else if (assist.State == EnergyComplianceService.BannerState.Red)
                        box.Background = new SolidColorBrush(Color.FromRgb(252, 231, 243));
                }
            }

            if (item.Required)
                tips.Add("Required.");
            if (ItemGroupLayout.IsRequiredMissing(item))
            {
                box.BorderBrush = GridMissingBorder;
                box.BorderThickness = new Thickness(2);
                tips.Add(ItemGroupLayout.RequiredMissingText);
            }
            if (ReferenceEquals(item, _editorLoadedItem))
            {
                box.BorderBrush = new SolidColorBrush(Color.FromRgb(0, 122, 153));
                box.BorderThickness = new Thickness(2);
            }
            box.ToolTip = string.Join(Environment.NewLine, tips);
        }

        private void ItemGroupCell_GotFocus(ItemGroupCell cell, TextBox box, bool inPane)
        {
            if (_itemGroupCellRefreshing)
                return;
            box.SelectAll();
            if (ReferenceEquals(cell.Item, _editorLoadedItem))
                return;

            // Select the cell's item so the right pane shows its editor (number pad or time box). In the classic pane
            // the grid itself is rebuilt by LoadItemEditor, so focus moves to the rebuilt box afterwards.
            AutoApplyCurrentItem();
            Item? previous = _editorLoadedItem;
            LoadItemEditor(cell.Item);
            if (previous != null)
                RefreshItemGroupCell(previous);
            RefreshItemGroupCell(cell.Item);
            if (inPane)
            {
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() =>
                {
                    if (_paneGridCellBoxes.TryGetValue(cell.Item, out var rebuilt) && rebuilt.IsLoaded)
                    {
                        _itemGroupCellRefreshing = true;
                        try { rebuilt.Focus(); rebuilt.SelectAll(); }
                        finally { _itemGroupCellRefreshing = false; }
                    }
                }));
            }
        }

        private void CommitItemGroupCell(ItemGroupCell cell, TextBox box)
        {
            if (_itemGroupCellRefreshing || _readOnlyMode || cell.Kind == ItemGroupCellKind.Result)
                return;
            string shown = ItemGroupLayout.DisplayValue(cell);
            if (string.Equals(box.Text, shown, StringComparison.Ordinal))
                return; // Untouched: never rewrite a stored value (F-002 Mode A, A-005-03).

            string stored;
            if (cell.Kind == ItemGroupCellKind.Time)
            {
                if (!ItemGroupLayout.TryParseTruckTime(box.Text, out stored))
                {
                    MessageBox.Show(this, "Enter a time such as 8:25 or 20:25.", "Arrival time", MessageBoxButton.OK, MessageBoxImage.Information);
                    RevertItemGroupCell(cell, box);
                    return;
                }
            }
            else if (!ItemGroupLayout.TryNormalizeNumber(box.Text, out stored, out string? error))
            {
                if (error == ItemGroupLayout.ValueTooLargeText)
                    MessageBox.Show(this, ItemGroupLayout.ValueTooLargeText, ItemGroupLayout.ValueTooLargeCaption, MessageBoxButton.OK, MessageBoxImage.Warning);
                else
                    MessageBox.Show(this, "Enter a number.", "Number", MessageBoxButton.OK, MessageBoxImage.Information);
                RevertItemGroupCell(cell, box);
                return;
            }

            ApplyItemGroupCellValue(cell.Item, stored);
        }

        private void RevertItemGroupCell(ItemGroupCell cell, TextBox box)
        {
            _itemGroupCellRefreshing = true;
            try { box.Text = ItemGroupLayout.DisplayValue(cell); }
            finally { _itemGroupCellRefreshing = false; }
        }

        // C1: a grid edit changes only the edited item's Value (plus the row's calculated Result for drainage).
        private void ApplyItemGroupCellValue(Item item, string stored)
        {
            if (EditorEditService.SetValue(_currentInspection, item, stored))
                MarkUnsaved();
            OnItemGroupValueChanged(item);
            if (ReferenceEquals(item, _editorLoadedItem) && !_isLoadingEditor)
            {
                LoadStatusControls(item);
                RefreshEngDataPanel();
                RefreshEcDataPanel();
            }
        }

        // Called for every value capture (right-pane number pad included): recompute a drainage Result and mirror the
        // value into the drawn cells. A no-op for items outside drawn grids.
        private void OnItemGroupValueChanged(Item item)
        {
            if (!ItemGroupLayout.IsGroupedItem(item))
                return;
            var run = ItemGroupLayout.FindRun(_currentInspection, item);
            if (run is not { Recognized: true })
                return;
            var resultItem = ItemGroupLayout.UpdateDrainageResult(run, item);
            RefreshItemGroupCell(item);
            RefreshItemGroupTreeNode(run);
            if (resultItem != null)
            {
                MarkUnsaved();
                RefreshItemGroupCell(resultItem);
                if (ReferenceEquals(resultItem, _editorLoadedItem) && !_isLoadingEditor)
                    LoadStatusControls(resultItem);
            }
        }

        // Returns true when the item is drawn as a grid cell (then the inline row refresh is replaced by this).
        private bool RefreshItemGroupCell(Item? item)
        {
            if (item == null)
                return false;
            var cell = ItemGroupLayout.FindCell(_currentInspection, item);
            if (cell == null)
                return false;
            var section = _currentInspection?.Sections.FirstOrDefault(s => s.Items.Contains(item, ReferenceEqualityComparer.Instance));
            _itemGroupCellRefreshing = true;
            try
            {
                foreach (var boxes in new[] { _inlineGridCellBoxes, _paneGridCellBoxes })
                {
                    if (!boxes.TryGetValue(item, out var box))
                        continue;
                    string shown = ItemGroupLayout.DisplayValue(cell);
                    if (!box.IsKeyboardFocusWithin && box.Text != shown)
                        box.Text = shown;
                    if (section != null)
                        ApplyItemGroupCellVisual(section, cell, box);
                }
            }
            finally { _itemGroupCellRefreshing = false; }
            return true;
        }

        // Right-pane editor for a grid cell. Returns false for items outside drawn grids (the normal editors run).
        private bool TryLoadItemGroupCellEditor(Item item)
        {
            var run = ItemGroupLayout.FindRun(_currentInspection, item);
            var cell = run is { Recognized: true } ? run.CellFor(item) : null;
            // A-005-08: in the right pane too, an unrecognised grid says so above the item's normal editor.
            if (run is { Recognized: false, Problem: not null })
                StatusPanel.Children.Add(CreateItemGroupNotice(run));
            if (run == null || cell == null)
                return false;

            DuplicateItemButton.Visibility = Visibility.Collapsed;
            RemoveDuplicateButton.Visibility = Visibility.Collapsed;
            NiValueButton.Visibility = Visibility.Collapsed;
            StatusLabel.Text = $"{run.Title.ToUpperInvariant()}: {run.RowLabels[cell.Row]}, {run.ColumnHeaders[cell.Column]}";

            if (!_inlineEditorMode)
            {
                _paneGridCellBoxes.Clear();
                StatusPanel.Children.Add(CreateItemGroupGrid(run.Section, run, inPane: true));
            }

            switch (cell.Kind)
            {
                case ItemGroupCellKind.Result:
                    StatusQuickPanel.Children.Add(new TextBlock
                    {
                        Text = string.IsNullOrEmpty(item.Value?.ToString()) ? "(no result yet)" : item.Value!.ToString(),
                        FontSize = 16,
                        FontWeight = FontWeights.Bold,
                        Foreground = item.Value?.ToString() == "Fail" ? GridFailBrush : GridPassBrush,
                        Margin = new Thickness(0, 4, 0, 4)
                    });
                    StatusQuickPanel.Children.Add(CreateItemGroupHint("Calculated from Expected and Shot Elevation; not edited by hand."));
                    break;

                case ItemGroupCellKind.Time:
                    var timeBox = new TextBox
                    {
                        Text = ItemGroupLayout.DisplayValue(cell),
                        Width = 110,
                        FontSize = 16,
                        Padding = new Thickness(6, 4, 6, 4),
                        IsReadOnly = _readOnlyMode,
                        HorizontalContentAlignment = HorizontalAlignment.Center,
                        ToolTip = "Type 8:25 or 20:25. Stored as 08:25 AM."
                    };
                    timeBox.LostKeyboardFocus += (_, _) => CommitItemGroupCell(cell, timeBox);
                    timeBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitItemGroupCell(cell, timeBox); e.Handled = true; } };
                    StatusQuickPanel.Children.Add(timeBox);
                    var nowButton = new Button { Content = "Now", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(6, 0, 0, 0), IsEnabled = !_readOnlyMode };
                    nowButton.Click += (_, _) => ApplyItemGroupCellValue(item, DateTime.Now.ToString("hh:mm tt", System.Globalization.CultureInfo.GetCultureInfo("en-US")));
                    StatusQuickPanel.Children.Add(nowButton);
                    var clearButton = new Button { Content = "Clear", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(6, 0, 0, 0), IsEnabled = !_readOnlyMode };
                    clearButton.Click += (_, _) => ApplyItemGroupCellValue(item, "");
                    StatusQuickPanel.Children.Add(clearButton);
                    break;

                default:
                    // Same number pad as NumberPad items, so its rules apply to grid cells too (F-004 §4).
                    StatusTextBox.Text = item.Value?.ToString() ?? "";
                    StatusTextBox.Visibility = Visibility.Visible;
                    StatusTextBox.IsReadOnly = true;
                    StatusTextBox.Background = new SolidColorBrush(Color.FromRgb(245, 245, 245));
                    // F-004 §4 key set: INSPECT's keypad has no "." or backspace (N3: digits append only without a point).
                    foreach (var digit in new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "¼", "½", "¾", "CLEAR" })
                    {
                        bool isFraction = digit is "½" or "¼" or "¾";
                        bool isAction = digit is "CLEAR";
                        var btn = new Button
                        {
                            Content = digit,
                            Width = digit == "CLEAR" ? 92 : 44,
                            Height = 44,
                            Margin = new Thickness(2),
                            FontSize = isFraction ? 15 : 18,
                            FontWeight = FontWeights.Bold,
                            IsEnabled = !_readOnlyMode,
                            Background = isAction
                                ? new SolidColorBrush(Color.FromRgb(255, 200, 200))
                                : isFraction
                                    ? new SolidColorBrush(Color.FromRgb(200, 220, 245))
                                    : new SolidColorBrush(Color.FromRgb(230, 230, 230)),
                            BorderBrush = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                            BorderThickness = new Thickness(1)
                        };
                        btn.Click += NumberPadButton_Click;
                        StatusKeyPanel.Children.Add(btn);
                    }
                    break;
            }

            UpdateRequiredFieldHighlighting(item);
            UpdateClearButton();
            if (cell.Kind == ItemGroupCellKind.Result)
                ClearValueButton.Visibility = Visibility.Collapsed;
            return true;
        }

        // Classic tree: one node per drawn grid (Tag = the run's first item) instead of one node per cell.
        private static string ItemGroupTreeNodeText(ItemGroupRun run) =>
            $"{run.Title} grid ({run.Items.Count(i => !string.IsNullOrWhiteSpace(i.Value?.ToString()))} of {run.Items.Count} filled)";

        // Hands-on pass 2026-10-19: the node's "N of M filled" follows edits instead of waiting for the tree to rebuild.
        private void RefreshItemGroupTreeNode(ItemGroupRun run)
        {
            if (_inlineEditorMode || run.Items.Count == 0)
                return;
            foreach (var sectionNode in SectionsTreeView.Items.OfType<TreeViewItem>())
                foreach (var node in sectionNode.Items.OfType<TreeViewItem>())
                    if (ReferenceEquals(node.Tag, run.Items[0]) && node.Header is StackPanel { Children.Count: 2 } header &&
                        header.Children[1] is TextBlock text)
                        text.Text = ItemGroupTreeNodeText(run);
        }

        private TreeViewItem CreateItemGroupTreeNode(ItemGroupRun run)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
            row.Children.Add(new TextBlock
            {
                Text = "▦",
                FontSize = _checklistFontSize,
                Margin = new Thickness(0, 0, 8, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(37, 99, 235)),
                VerticalAlignment = VerticalAlignment.Center
            });
            row.Children.Add(new TextBlock
            {
                Text = ItemGroupTreeNodeText(run),
                FontSize = _checklistFontSize - 1,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            });
            return new TreeViewItem
            {
                Header = row,
                Tag = run.Items[0],
                ToolTip = "Select to edit this grid in the right pane"
            };
        }

        private bool IsItemGroupGridFirstItem(Section section, Item item, out ItemGroupRun? run)
        {
            run = ItemGroupLayout.Detect(section).FirstOrDefault(candidate => candidate.Recognized && candidate.CellFor(item) != null);
            return run != null && ReferenceEquals(run.Items[0], item);
        }
    }
}
