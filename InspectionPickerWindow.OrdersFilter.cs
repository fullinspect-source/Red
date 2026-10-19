using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using InspectionEditor.Services;

namespace InspectionEditor
{
    // ORDERS filter row, like Inspect's DevExpress auto-filter row: one box per column with an
    // operator menu (Equals ... Is less than or equal to). Contains is the default, case-insensitive.
    public partial class InspectionPickerWindow
    {
        private enum OrderFilterOp
        {
            Equals, NotEquals, Contains, NotContains, Like, NotLike,
            BeginsWith, EndsWith, Greater, GreaterOrEqual, Less, LessOrEqual
        }

        private static readonly (OrderFilterOp Op, string Name, string Glyph)[] OrderFilterOps =
        {
            (OrderFilterOp.Equals,         "Equals",                   "="),
            (OrderFilterOp.NotEquals,      "Does not equal",           "≠"),
            (OrderFilterOp.Contains,       "Contains",                 "~"),
            (OrderFilterOp.NotContains,    "Does not contain",         "!~"),
            (OrderFilterOp.Like,           "Is like (% and _ wildcards)", "%"),
            (OrderFilterOp.NotLike,        "Is not like",              "!%"),
            (OrderFilterOp.BeginsWith,     "Begins with",              "a…"),
            (OrderFilterOp.EndsWith,       "Ends with",                "…a"),
            (OrderFilterOp.Greater,        "Is greater than",          ">"),
            (OrderFilterOp.GreaterOrEqual, "Is greater than or equal to", "≥"),
            (OrderFilterOp.Less,           "Is less than",             "<"),
            (OrderFilterOp.LessOrEqual,    "Is less than or equal to", "≤"),
        };

        private sealed class OrderColumnFilter
        {
            public OrderFilterOp Op = OrderFilterOp.Contains;
            public string Text = "";
            public bool IsActive => Text.Trim().Length > 0;
        }

        private const string ScheduleDateColumn = "Schedule Date";

        // Column title -> the text the column shows.
        private static readonly Dictionary<string, Func<StrandScheduleRequest, string>> _orderFilterValues = new()
        {
            { "Job",               r => r.JobNumber },
            { "Address",           r => r.Address },
            { "Subdivision",       r => r.SubdivisionName },
            { "Builder",           r => r.BuilderName },
            { "City",              r => r.City },
            { "Service",           r => r.ServiceName },
            { ScheduleDateColumn,  r => r.ScheduleDateText },
            { "Assigned",          r => r.AssignedName },
            { "Note",              r => r.SpecialRequest },
        };

        private readonly Dictionary<string, OrderColumnFilter> _orderFilters = new(StringComparer.Ordinal);
        private readonly HashSet<TextBox> _orderFilterBoxes = new();
        private readonly HashSet<Button> _orderFilterOpButtons = new();
        private DispatcherTimer? _orderFilterTimer;
        private bool _resettingOrderFilters;

        private OrderColumnFilter GetOrderFilter(string column)
        {
            if (!_orderFilters.TryGetValue(column, out var filter))
                _orderFilters[column] = filter = new OrderColumnFilter();
            return filter;
        }

        private bool HasActiveOrderFilters => _orderFilters.Values.Any(f => f.IsActive);

        private bool OrdersFilterPredicate(object item)
        {
            if (item is not StrandScheduleRequest request)
                return true;

            foreach (var (column, filter) in _orderFilters)
            {
                if (!filter.IsActive || !_orderFilterValues.TryGetValue(column, out var getValue))
                    continue;
                if (!MatchesOrderFilter(request, column, getValue(request) ?? "", filter))
                    return false;
            }
            return true;
        }

        private static bool MatchesOrderFilter(StrandScheduleRequest request, string column, string value, OrderColumnFilter filter) =>
            MatchesColumnFilter(value, filter, column == ScheduleDateColumn, request.LocalDate);

        // One column's test, shared by ORDERS and the F-025 Buyer Walk filter row (OC §6). isDate: the column shows a
        // date, so Equals/Not equals and the comparisons use the underlying date value.
        private static bool MatchesColumnFilter(string value, OrderColumnFilter filter, bool isDate, DateTime? date)
        {
            string text = filter.Text.Trim();
            const StringComparison ignoreCase = StringComparison.CurrentCultureIgnoreCase;

            // A typed date equals every request on that day, timed or not (the same day rule as > and <).
            if (isDate && filter.Op is OrderFilterOp.Equals or OrderFilterOp.NotEquals)
            {
                bool same = string.Equals(value, text, ignoreCase) ||
                            CompareForColumnFilter(value, text, isDate, date) == 0;
                return filter.Op == OrderFilterOp.Equals ? same : !same;
            }

            switch (filter.Op)
            {
                case OrderFilterOp.Equals: return string.Equals(value, text, ignoreCase);
                case OrderFilterOp.NotEquals: return !string.Equals(value, text, ignoreCase);
                case OrderFilterOp.Contains: return value.Contains(text, ignoreCase);
                case OrderFilterOp.NotContains: return !value.Contains(text, ignoreCase);
                case OrderFilterOp.Like: return LikeRegex(text).IsMatch(value);
                case OrderFilterOp.NotLike: return !LikeRegex(text).IsMatch(value);
                case OrderFilterOp.BeginsWith: return value.StartsWith(text, ignoreCase);
                case OrderFilterOp.EndsWith: return value.EndsWith(text, ignoreCase);
            }

            // Greater / less: dates compare as dates, numbers as numbers, anything else as text. Blank never matches.
            int? comparison = CompareForColumnFilter(value, text, isDate, date);
            if (comparison is not int c)
                return false;
            return filter.Op switch
            {
                OrderFilterOp.Greater => c > 0,
                OrderFilterOp.GreaterOrEqual => c >= 0,
                OrderFilterOp.Less => c < 0,
                OrderFilterOp.LessOrEqual => c <= 0,
                _ => true
            };
        }

        private static int? CompareForColumnFilter(string value, string text, bool isDate, DateTime? date)
        {
            if (isDate)
            {
                if (date is not DateTime when ||
                    !DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out var wanted))
                {
                    return null;
                }
                // A date without a time compares by day, so "> 10/20/2026" means after that day.
                bool dayOnly = wanted.TimeOfDay == TimeSpan.Zero && !text.Contains(':');
                return dayOnly ? when.Date.CompareTo(wanted.Date) : when.CompareTo(wanted);
            }

            if (value.Length == 0)
                return null;
            if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.CurrentCulture, out var left) &&
                decimal.TryParse(text, NumberStyles.Number, CultureInfo.CurrentCulture, out var right))
            {
                return left.CompareTo(right);
            }
            return string.Compare(value, text, CultureInfo.CurrentCulture, CompareOptions.IgnoreCase);
        }
        // "Is like": % matches any run of characters, _ matches one; the whole value must match.
        private static Regex LikeRegex(string pattern) =>
            new("^" + Regex.Escape(pattern).Replace("%", ".*").Replace("_", ".") + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline);

        private void OrdersFilterBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is not TextBox box || box.DataContext is not string column)
                return;
            _orderFilterBoxes.Add(box);
            if (_orderFilters.TryGetValue(column, out var filter) && box.Text != filter.Text)
            {
                _resettingOrderFilters = true;
                try { box.Text = filter.Text; }
                finally { _resettingOrderFilters = false; }
            }
        }

        private void OrdersFilterBox_Unloaded(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox box)
                _orderFilterBoxes.Remove(box);
        }

        // WPF lays headers out right-to-left, so Tab into the grid would land on the last (off-screen) column.
        // Send it to the Job filter instead.
        private void OrdersListView_PreviewGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (e.NewFocus is not TextBox box || !_orderFilterBoxes.Contains(box) ||
                !Keyboard.IsKeyDown(Key.Tab) || (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ||
                (e.OldFocus is Visual old && OrdersListView.IsAncestorOf(old)) ||
                Equals(box.DataContext, "Job"))
            {
                return;
            }

            var job = _orderFilterBoxes.FirstOrDefault(b => b.IsVisible && Equals(b.DataContext, "Job"));
            if (job == null)
                return;
            e.Handled = true;
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { job.Focus(); job.SelectAll(); });
        }

        // Tab / Shift+Tab move between filter boxes in column order, like Inspect's filter row.
        private void OrdersFilterBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Tab || sender is not TextBox box || OrdersListView.View is not GridView view)
                return;

            var ordered = _orderFilterBoxes
                .Where(b => b.IsVisible && b.DataContext is string)
                .OrderBy(b => view.Columns.TakeWhile(c => !Equals(c.Header, b.DataContext)).Count())
                .ToList();
            int next = ordered.IndexOf(box) + ((Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1);
            if (next < 0 || next >= ordered.Count)
                return; // at either end, Tab leaves the grid as usual

            ordered[next].Focus();
            ordered[next].SelectAll();
            e.Handled = true;
        }

        private void OrdersFilterBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_resettingOrderFilters || sender is not TextBox { DataContext: string column } box)
                return;
            GetOrderFilter(column).Text = box.Text;
            ScheduleOrderFilterRefresh();
        }

        private void OrdersFilterOpButton_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is Button { DataContext: string column } button)
            {
                _orderFilterOpButtons.Add(button);
                ShowOrderFilterOp(button, GetOrderFilter(column).Op);
            }
        }

        private void OrdersFilterOpButton_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true; // keep the header from treating this as a sort click
            if (sender is not Button { DataContext: string column } button)
                return;

            var filter = GetOrderFilter(column);
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
                    ApplyOrderFilters();
                };
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        }

        private static void ShowOrderFilterOp(Button button, OrderFilterOp op)
        {
            var entry = OrderFilterOps.First(o => o.Op == op);
            button.Content = entry.Glyph;
            button.ToolTip = entry.Name;
        }

        private void ScheduleOrderFilterRefresh()
        {
            if (_orderFilterTimer == null)
            {
                _orderFilterTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                _orderFilterTimer.Tick += (_, _) =>
                {
                    _orderFilterTimer.Stop();
                    ApplyOrderFilters();
                };
            }
            _orderFilterTimer.Stop();
            _orderFilterTimer.Start();
        }

        private void ApplyOrderFilters()
        {
            if (_pickerClosed)
                return;
            CollectionViewSource.GetDefaultView(OrdersListView.ItemsSource)?.Refresh();
            UpdateOrderFilterUi();
            UpdateOrdersSelectionUi();
        }

        private void UpdateOrderFilterUi()
        {
            bool active = HasActiveOrderFilters;
            OrdersClearFiltersButton.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            OrdersFilterText.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
            if (!active)
                return;

            int total = (OrdersListView.ItemsSource as ICollection<StrandScheduleRequest>)?.Count ?? 0;
            int shown = OrdersListView.Items.Count;
            OrdersFilterText.Text = $"Filtered: showing {shown} of {total}";
        }

        private void OrdersClearFiltersButton_Click(object sender, RoutedEventArgs e)
        {
            _orderFilterTimer?.Stop();
            _orderFilters.Clear();
            _resettingOrderFilters = true;
            try
            {
                foreach (var box in _orderFilterBoxes)
                    box.Text = "";
            }
            finally { _resettingOrderFilters = false; }
            foreach (var button in _orderFilterOpButtons)
                ShowOrderFilterOp(button, OrderFilterOp.Contains);
            ApplyOrderFilters();
        }
    }
}
