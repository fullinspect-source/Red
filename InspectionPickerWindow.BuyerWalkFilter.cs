using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using InspectionEditor.Services;

namespace InspectionEditor
{
    // F-025 §6: the ORDERS filter row (12 operators, Filtered label, Clear Filters) over Buyer Walk columns 1-10. The
    // operators and the column test are ORDERS' own (InspectionPickerWindow.OrdersFilter.cs, MatchesColumnFilter).
    public partial class InspectionPickerWindow
    {
        // Column title -> (shown text, date value for the four date columns).
        private static readonly Dictionary<string, (Func<StrandBuyerWalk, string> Text, Func<StrandBuyerWalk, DateTime?>? Date)> _buyerWalkFilterValues = new()
        {
            { "Job",         (r => r.JobNumber ?? "", null) },
            { "Address",     (r => r.Address ?? "", null) },
            { "Subdivision", (r => r.Subdivision ?? "", null) },
            { "City",        (r => r.City ?? "", null) },
            { "Ready",       (r => r.ReadyText, r => r.ReadyDate) },
            { "Closing",     (r => r.ClosingText, r => r.ClosingDate) },
            { "Inspector",   (r => r.InspectorName ?? "", null) },
            { "1st",         (r => r.FirstText, r => r.ScheduleDate) },
            { "2nd",         (r => r.SecondText, r => r.Schedule2Date) },
            { "Note",        (r => r.Note ?? "", null) },
        };

        private readonly Dictionary<string, OrderColumnFilter> _buyerWalkFilters = new(StringComparer.Ordinal);
        private readonly HashSet<TextBox> _buyerWalkFilterBoxes = new();
        private readonly HashSet<Button> _buyerWalkFilterOpButtons = new();
        private DispatcherTimer? _buyerWalkFilterTimer;
        private bool _resettingBuyerWalkFilters;

        private OrderColumnFilter GetBuyerWalkFilter(string column)
        {
            if (!_buyerWalkFilters.TryGetValue(column, out var filter))
                _buyerWalkFilters[column] = filter = new OrderColumnFilter();
            return filter;
        }

        private bool BuyerWalkFilterPredicate(object item)
        {
            if (item is not StrandBuyerWalk row)
                return true;
            foreach (var (column, filter) in _buyerWalkFilters)
            {
                if (!filter.IsActive || !_buyerWalkFilterValues.TryGetValue(column, out var value))
                    continue;
                if (!MatchesColumnFilter(value.Text(row), filter, value.Date != null, value.Date?.Invoke(row)))
                    return false;
            }
            return true;
        }

        private void BuyerWalkFilterBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is not TextBox box || box.DataContext is not string column)
                return;
            _buyerWalkFilterBoxes.Add(box);
            if (_buyerWalkFilters.TryGetValue(column, out var filter) && box.Text != filter.Text)
            {
                _resettingBuyerWalkFilters = true;
                try { box.Text = filter.Text; }
                finally { _resettingBuyerWalkFilters = false; }
            }
        }

        private void BuyerWalkFilterBox_Unloaded(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox box)
                _buyerWalkFilterBoxes.Remove(box);
        }

        private void BuyerWalkFilterBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_resettingBuyerWalkFilters || sender is not TextBox { DataContext: string column } box)
                return;
            GetBuyerWalkFilter(column).Text = box.Text;
            if (_buyerWalkFilterTimer == null)
            {
                _buyerWalkFilterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                _buyerWalkFilterTimer.Tick += (_, _) =>
                {
                    _buyerWalkFilterTimer.Stop();
                    ApplyBuyerWalkFilters();
                };
            }
            _buyerWalkFilterTimer.Stop();
            _buyerWalkFilterTimer.Start();
        }

        private void BuyerWalkFilterOpButton_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is Button { DataContext: string column } button)
            {
                _buyerWalkFilterOpButtons.Add(button);
                ShowOrderFilterOp(button, GetBuyerWalkFilter(column).Op);
            }
        }

        private void BuyerWalkFilterOpButton_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true; // never a sort click
            if (sender is not Button { DataContext: string column } button)
                return;
            var filter = GetBuyerWalkFilter(column);
            var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
            foreach (var (op, name, glyph) in OrderFilterOps)
            {
                var item = new MenuItem
                {
                    Header = name.Replace("_", "__"), // a single "_" is an access key and would hide the "_" wildcard
                    Icon = new TextBlock { Text = glyph, Width = 22, TextAlignment = TextAlignment.Center },
                    IsCheckable = true,
                    IsChecked = op == filter.Op
                };
                item.Click += (_, _) =>
                {
                    filter.Op = op;
                    ShowOrderFilterOp(button, op);
                    ApplyBuyerWalkFilters();
                };
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        }

        // A row hidden by a filter leaves the selection at once (OC §5.10 D-23).
        private void ApplyBuyerWalkFilters()
        {
            if (_pickerClosed)
                return;
            CollectionViewSource.GetDefaultView(BuyerWalkListView.ItemsSource)?.Refresh();
            foreach (var hidden in BuyerWalkListView.SelectedItems.OfType<StrandBuyerWalk>().Where(r => !BuyerWalkFilterPredicate(r)).ToList())
                BuyerWalkListView.SelectedItems.Remove(hidden);
            UpdateBuyerWalkSelectionUi();
        }

        private void UpdateBuyerWalkFilterUi()
        {
            bool active = _buyerWalkFilters.Values.Any(f => f.IsActive);
            BuyerWalkClearFiltersButton.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            BuyerWalkFilterText.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            if (!active)
                return;
            int total = (BuyerWalkListView.ItemsSource as ICollection<StrandBuyerWalk>)?.Count ?? 0;
            BuyerWalkFilterText.Text = $"Filtered: showing {BuyerWalkListView.Items.Count} of {total}";
        }

        private void BuyerWalkClearFiltersButton_Click(object sender, RoutedEventArgs e)
        {
            _buyerWalkFilterTimer?.Stop();
            _buyerWalkFilters.Clear();
            _resettingBuyerWalkFilters = true;
            try
            {
                foreach (var box in _buyerWalkFilterBoxes)
                    box.Text = "";
            }
            finally { _resettingBuyerWalkFilters = false; }
            foreach (var button in _buyerWalkFilterOpButtons)
                ShowOrderFilterOp(button, OrderFilterOp.Contains);
            ApplyBuyerWalkFilters();
        }
    }
}
