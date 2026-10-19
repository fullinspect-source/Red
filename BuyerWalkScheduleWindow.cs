using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using InspectionEditor.Services;

namespace InspectionEditor
{
    // F-025 §9 Edit Schedule (INSPECT EditScheduleWindow): two columns, E2 fields and limits, E5-E8 through the
    // BuyerWalkSheet rules (never while filling), E3 validation, Save (B3), Cancel and Add to My List (A5).
    public sealed class BuyerWalkScheduleWindow : Window
    {
        private static readonly CultureInfo EnUs = CultureInfo.GetCultureInfo("en-US");
        private readonly BuyerWalkSheet _sheet;
        private readonly StrandBuyerWalkService _service;
        private bool _syncing, _closingAfterAdd;
        private int _busyCount;
        private string _scheduledText = "";
        private bool Busy => _busyCount > 0;

        private readonly TextBox _buyerName = Box(100), _buyerPhone1 = Phone(), _buyerPhone2 = Phone(), _buyerEmail = Box(250),
            _agentName = ReadOnlyBox(), _agentPhone = ReadOnlyBox(), _agentEmail = ReadOnlyBox(),
            _super = Box(100), _superPhone = Phone(), _superEmail = Box(250), _salesEmail = Box(0),
            _requests = Box(1000, multiLine: true), _note = Box(0), _history = ReadOnlyBox(multiLine: true),
            _job = ReadOnlyBox(), _ordered = ReadOnlyBox(), _scheduled = Box(0);
        private readonly ComboBox _status = new() { DisplayMemberPath = nameof(StrandScheduleStatus.Name), MinWidth = 200 };
        private readonly ComboBox _inspector = new() { DisplayMemberPath = nameof(StrandBuyerWalkInspector.DisplayName), MinWidth = 200 };
        private readonly DatePicker _ready = new() { MinWidth = 160 }, _closing = new() { MinWidth = 160 };
        private readonly CheckBox _noAnswer = new() { Content = " No answer. Attempt:", VerticalAlignment = VerticalAlignment.Center };
        private readonly CheckBox _leftMessage = new() { Content = " Left message. Attempt:", VerticalAlignment = VerticalAlignment.Center };
        private readonly ComboBox _noAnswerCount = Counter(), _leftMessageCount = Counter();
        private readonly ToggleButton _flag = new() { MinWidth = 120, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(10, 0, 0, 0) };
        private readonly Button _add = new() { Content = "Add to My List", Padding = new Thickness(14, 6, 14, 6) };
        private readonly Button _save = new() { Content = "Save", Padding = new Thickness(18, 6, 18, 6), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        private readonly Button _cancel = new() { Content = "Cancel", Padding = new Thickness(14, 6, 14, 6), IsCancel = true };

        public bool Saved { get; private set; }
        public Func<Task>? AddToMyListRequested { get; set; }

        public BuyerWalkScheduleWindow(BuyerWalkSheet sheet, string address, StrandBuyerWalkService service)
        {
            _sheet = sheet;
            _service = service;
            Title = "Edit Schedule";
            Width = 1100;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ResizeMode = ResizeMode.NoResize;
            Background = new SolidColorBrush(Color.FromRgb(246, 247, 248));

            _status.ItemsSource = sheet.Statuses;
            _inspector.ItemsSource = sheet.Inspectors;
            Fill();
            Wire();

            var left = new Grid();
            left.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            left.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            int row = 0;
            void L(string label, UIElement field) => AddRow(left, ref row, label, field);
            L("Buyer Name:", _buyerName);
            L("Buyer Phone:", Pair(_buyerPhone1, "Phone:", _buyerPhone2));
            L("Buyer Email:", _buyerEmail);
            L("Agent Name:", Pair(_agentName, "Phone:", _agentPhone));
            L("Agent Email:", _agentEmail);
            L("Super:", Pair(_super, "Phone:", _superPhone));
            L("Super Email:", _superEmail);
            L("Sales Email:", _salesEmail);
            L("Requests:", _requests);
            var scheduling = new WrapPanel();
            scheduling.Children.Add(_noAnswer);
            scheduling.Children.Add(_noAnswerCount);
            scheduling.Children.Add(_leftMessage);
            scheduling.Children.Add(_leftMessageCount);
            scheduling.Children.Add(_flag);
            L("Scheduling:", scheduling);
            L("Note:", _note);
            L("History:", _history);

            var right = new Grid();
            right.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            right.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row = 0;
            void R(string label, UIElement field) => AddRow(right, ref row, label, field);
            R("Job:", _job);
            R("Status:", _status);
            R("Inspector:", _inspector);
            R("Ordered:", _ordered);
            R("Ready:", _ready);
            R("Est Closing:", _closing);
            R("Scheduled:", _scheduled);
            right.Children.Add(new TextBlock
            {
                Text = "Scheduled: type M/d/yyyy hh:mm AM/PM, then Tab.",
                Foreground = Brushes.DimGray, FontSize = 12, Margin = new Thickness(0, 2, 0, 0)
            });
            Grid.SetRow(right.Children[^1], row);
            Grid.SetColumn(right.Children[^1], 1);
            right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var columns = new Grid { Margin = new Thickness(0, 8, 0, 0) };
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3, GridUnitType.Star) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            columns.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(2, GridUnitType.Star) });
            columns.Children.Add(left);
            Grid.SetColumn(right, 2);
            columns.Children.Add(right);

            var buttons = new DockPanel { Margin = new Thickness(0, 14, 0, 0), LastChildFill = false };
            DockPanel.SetDock(_add, Dock.Left);
            buttons.Children.Add(_add);
            var trailing = new StackPanel { Orientation = Orientation.Horizontal };
            trailing.Children.Add(_save);
            trailing.Children.Add(_cancel);
            DockPanel.SetDock(trailing, Dock.Right);
            buttons.Children.Add(trailing);

            var root = new StackPanel { Margin = new Thickness(18) };
            root.Children.Add(new TextBlock { Text = address, FontSize = 20, FontWeight = FontWeights.SemiBold });
            root.Children.Add(columns);
            root.Children.Add(buttons);
            Content = root;
        }

        private static TextBox Box(int max, bool multiLine = false) => new()
        {
            MaxLength = max, Padding = new Thickness(4, 3, 4, 3), AcceptsReturn = multiLine, TextWrapping = multiLine ? TextWrapping.Wrap : TextWrapping.NoWrap,
            MinHeight = multiLine ? 70 : 0, VerticalScrollBarVisibility = multiLine ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled
        };

        private static TextBox ReadOnlyBox(bool multiLine = false)
        {
            var box = Box(0, multiLine);
            box.IsReadOnly = true;
            box.Background = new SolidColorBrush(Color.FromRgb(238, 241, 244));
            return box;
        }

        // §9.3 phone input: (ddd) AAA-AAAA, d a digit, A a digit or capital letter.
        private static TextBox Phone()
        {
            var box = Box(15);
            box.Tag = "phone";
            return box;
        }

        private static ComboBox Counter()
        {
            var box = new ComboBox { Width = 52, Margin = new Thickness(4, 0, 12, 0) };
            for (int n = 1; n <= 4; n++) box.Items.Add(n);
            return box;
        }

        private static FrameworkElement Pair(TextBox first, string label, TextBox second)
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
            grid.Children.Add(first);
            var text = new TextBlock { Text = label, Margin = new Thickness(10, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            Grid.SetColumn(second, 2);
            grid.Children.Add(second);
            return grid;
        }

        private static void AddRow(Grid grid, ref int row, string label, UIElement field)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var text = new TextBlock { Text = label, Margin = new Thickness(0, 6, 10, 0), VerticalAlignment = VerticalAlignment.Top, Foreground = new SolidColorBrush(Color.FromRgb(65, 72, 80)) };
            Grid.SetRow(text, row);
            grid.Children.Add(text);
            if (field is FrameworkElement element) element.Margin = new Thickness(0, 3, 0, 3);
            Grid.SetRow(field, row);
            Grid.SetColumn(field, 1);
            grid.Children.Add(field);
            row++;
        }

        // E1 step 3: every box from the record; nothing here runs a behaviour rule.
        private void Fill()
        {
            _syncing = true;
            try
            {
                var loaded = _sheet.Loaded;
                _buyerName.Text = _sheet.BuyerName;
                _buyerPhone1.Text = _sheet.BuyerPhone1;
                _buyerPhone2.Text = _sheet.BuyerPhone2;
                _buyerEmail.Text = _sheet.BuyerEmail;
                _agentName.Text = loaded.BuyerAgentName ?? "";
                _agentPhone.Text = loaded.BuyerAgentPhone ?? "";
                _agentEmail.Text = loaded.BuyerAgentEmail ?? "";
                _super.Text = _sheet.Superintendent;
                _superPhone.Text = _sheet.SuperintendentPhone;
                _superEmail.Text = _sheet.SuperintendentEmail;
                _salesEmail.Text = _sheet.SalesEmail;
                _requests.Text = _sheet.Requests;
                _history.Text = loaded.History ?? "";
                _job.Text = loaded.JobNumber ?? "";
                _ordered.Text = loaded.OrderText ?? "";
                _inspector.SelectedItem = _sheet.Inspectors.FirstOrDefault(i => i.Username == _sheet.InspectorUsername); // not in the list: blank
                _ready.SelectedDate = _sheet.Ready;
                _closing.SelectedDate = _sheet.Closing;
                Refresh();
            }
            finally { _syncing = false; }
        }

        // The boxes the rules can change: status, Scheduled, Note, counters, boxes, flag.
        private void Refresh()
        {
            bool was = _syncing;
            _syncing = true;
            try
            {
                _status.SelectedItem = _sheet.Statuses.FirstOrDefault(s => s.Id == _sheet.StatusId);
                _scheduled.Text = _scheduledText = StrandBuyerWalkService.SheetScheduledText(_sheet.Scheduled);
                if (_note.Text != _sheet.Note) _note.Text = _sheet.Note;
                _noAnswer.IsChecked = _sheet.NoAnswerChecked;
                _leftMessage.IsChecked = _sheet.LeftMessageChecked;
                _noAnswerCount.SelectedItem = _sheet.NoAnswerCount;
                _leftMessageCount.SelectedItem = _sheet.LeftMessageCount;
                _flag.IsChecked = _sheet.Flagged;
                _flag.Content = _sheet.Flagged ? "⚑ Flagged" : "⚐ Not flagged";
                _flag.Foreground = _sheet.Flagged ? Brushes.White : Brushes.DimGray;
                _flag.Background = _sheet.Flagged ? new SolidColorBrush(Color.FromRgb(200, 30, 30)) : new SolidColorBrush(Color.FromRgb(232, 236, 239));
                AutomationName(_flag, _sheet.Flagged ? "Flagged" : "Not flagged");
            }
            finally { _syncing = was; }
        }

        private static void AutomationName(DependencyObject element, string name) => System.Windows.Automation.AutomationProperties.SetName(element, name);

        private void Wire()
        {
            void Text(TextBox box, Action<string> set) => box.TextChanged += (_, _) => { if (!_syncing) set(box.Text); };
            Text(_buyerName, v => _sheet.BuyerName = v);
            Text(_buyerEmail, v => _sheet.BuyerEmail = v);
            Text(_super, v => _sheet.Superintendent = v);
            Text(_superEmail, v => _sheet.SuperintendentEmail = v);
            Text(_salesEmail, v => _sheet.SalesEmail = v);
            Text(_requests, v => _sheet.Requests = v);
            Text(_note, v => _sheet.Note = v);
            foreach (var (box, set) in new (TextBox, Action<string>)[] { (_buyerPhone1, v => _sheet.BuyerPhone1 = v), (_buyerPhone2, v => _sheet.BuyerPhone2 = v), (_superPhone, v => _sheet.SuperintendentPhone = v) })
            {
                box.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsLetterOrDigit);
                box.TextChanged += (_, _) =>
                {
                    if (_syncing) return;
                    string formatted = FormatPhone(box.Text);
                    if (formatted != box.Text)
                    {
                        _syncing = true;
                        try { box.Text = formatted; box.CaretIndex = formatted.Length; }
                        finally { _syncing = false; }
                    }
                    set(box.Text);
                };
            }
            _status.SelectionChanged += (_, _) =>
            {
                if (_syncing || _status.SelectedItem is not StrandScheduleStatus status) return;
                _sheet.SetStatus(status.Id); // E6
                Refresh();
            };
            _inspector.SelectionChanged += (_, _) =>
            {
                if (!_syncing && _inspector.SelectedItem is StrandBuyerWalkInspector inspector) _sheet.InspectorUsername = inspector.Username;
            };
            _ready.SelectedDateChanged += (_, _) => { if (!_syncing) _sheet.Ready = _ready.SelectedDate; };
            _closing.SelectedDateChanged += (_, _) => { if (!_syncing) _sheet.Closing = _closing.SelectedDate; };
            _scheduled.LostFocus += (_, _) => CommitScheduled();
            _scheduled.KeyDown += (_, e) => { if (e.Key == Key.Enter) { CommitScheduled(); e.Handled = true; } };
            _noAnswer.Click += (_, _) => { _sheet.SetNoAnswerChecked(_noAnswer.IsChecked == true); Refresh(); };
            _leftMessage.Click += (_, _) => { _sheet.SetLeftMessageChecked(_leftMessage.IsChecked == true); Refresh(); };
            _noAnswerCount.SelectionChanged += (_, _) => { if (!_syncing && _noAnswerCount.SelectedItem is int n) { _sheet.SetNoAnswerCount(n); Refresh(); } };
            _leftMessageCount.SelectionChanged += (_, _) => { if (!_syncing && _leftMessageCount.SelectedItem is int n) { _sheet.SetLeftMessageCount(n); Refresh(); } };
            _flag.Click += (_, _) => { _sheet.ToggleFlag(); Refresh(); }; // E8
            _save.Click += async (_, _) => { if (await SaveAsync()) Close(); };
            _cancel.Click += (_, _) => Close(); // E9: nothing sent
            // A5 runs as one unit: the sheet cannot be closed, saved or added again until it ends.
            _add.Click += async (_, _) =>
            {
                if (Busy || AddToMyListRequested == null) return;
                SetBusy(true);
                try { await AddToMyListRequested(); }
                finally { SetBusy(false); }
            };
        }

        // §9.3: digits and capital letters into (ddd) AAA-AAAA as the user types.
        public static string FormatPhone(string text)
        {
            var chars = new StringBuilder();
            foreach (char c in text.ToUpperInvariant())
            {
                if (chars.Length >= 10) break;
                if (chars.Length < 3 ? char.IsAsciiDigit(c) : char.IsAsciiDigit(c) || c is >= 'A' and <= 'Z')
                    chars.Append(c);
            }
            string s = chars.ToString();
            if (s.Length == 0) return "";
            if (s.Length <= 3) return "(" + s;
            if (s.Length <= 6) return $"({s[..3]}) {s[3..]}";
            return $"({s[..3]}) {s[3..6]}-{s[6..]}";
        }

        // E5 when a value is set; clearing the box runs no rule. Only an edited box is committed, so Save never re-runs E5 on
        // an untouched value (whose seconds the box does not show).
        private void CommitScheduled()
        {
            if (_syncing || _scheduled.Text.Trim() == _scheduledText.Trim()) return;
            string text = _scheduled.Text.Trim();
            DateTime? value = null;
            if (text.Length > 0)
            {
                string[] formats = { "M/d/yyyy hh:mm tt", "M/d/yyyy h:mm tt", "M/d/yyyy h:mmtt", "M/d/yyyy H:mm", "M/d/yyyy" };
                if (!DateTime.TryParseExact(text, formats, EnUs, DateTimeStyles.AllowWhiteSpaces, out var parsed))
                {
                    Refresh(); // not a date: back to the current value
                    return;
                }
                value = parsed;
            }
            if (value == _sheet.Scheduled) { Refresh(); return; }
            var warnings = _sheet.SetScheduled(value);
            Refresh();
            foreach (string warning in warnings)
                MessageBox.Show(this, warning, "", MessageBoxButton.OK, MessageBoxImage.Warning); // no title, warning style
        }

        // E3 then B3 (§9.6). False when it did not save (its alert shown); the sheet keeps the edits.
        public async Task<bool> SaveAsync(bool partOfAdd = false)
        {
            if (Busy && !partOfAdd) return false;
            CommitScheduled();
            var errors = _sheet.Validate();
            if (errors.Count > 0)
            {
                MessageBox.Show(this, BuyerWalkSheet.ValidationText(errors), "Error(s)", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
            SetBusy(true);
            string? failure;
            try { failure = await _service.UpdateAsync(_sheet.UpdateBody()); }
            catch (StrandAuthRequiredException ex) { failure = ex.Message; }
            finally { SetBusy(false); }
            if (failure != null)
            {
                MessageBox.Show(this, failure, "", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
            Saved = true; // E10: the list reloads when the sheet closes
            return true;
        }

        public void CloseAfterAdd()
        {
            Saved = true;
            _closingAfterAdd = true;
            Close();
        }

        // Never closed while B3, W2 or the file work is in flight (Esc, Cancel and the window X included).
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (Busy && !_closingAfterAdd)
                e.Cancel = true;
            base.OnClosing(e);
        }

        private void SetBusy(bool busy)
        {
            _busyCount = Math.Max(0, _busyCount + (busy ? 1 : -1));
            _save.IsEnabled = _add.IsEnabled = _cancel.IsEnabled = !Busy;
            Cursor = Busy ? Cursors.Wait : null;
        }
    }
}
