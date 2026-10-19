using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using InspectionEditor.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace InspectionEditor
{
    // F-021 Add Service: the four-page wizard (SPEC §3, MADD §2.2-§2.5). The work is Services\AddServiceService.cs.
    public sealed class AddServiceWindow : Window
    {
        private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(500);
        private readonly CompleteProfile _profile;
        private readonly string? _rawUserType;
        private readonly StrandWebsiteSession _session;
        private readonly JArray? _jobsCache, _formsCache, _templates;

        private int _page = 1;
        private bool _pageChanged;
        private DateTime _quietUntil = DateTime.MinValue;
        private bool _added;
        private JObject? _selectedJob;
        private AddFormRow? _selectedForm;
        private List<AddFormRow> _forms = new();
        private bool _updatingDate;

        private readonly TextBlock _title = new() { FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
        private readonly TextBlock _description = new() { Foreground = Brushes.DimGray, Margin = new Thickness(0, 0, 0, 10), TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _error = new() { Foreground = Brushes.Firebrick, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap };
        private readonly ContentControl _body = new();
        private readonly Button _back = new() { Content = "< Back", Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 0, 8, 0) };
        private readonly Button _next = new() { Content = "Next >", Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        private readonly Button _close = new() { Content = "Close", Padding = new Thickness(14, 5, 14, 5), Margin = new Thickness(0, 0, 8, 0) };
        private readonly Button _open = new() { Content = "Open", Padding = new Thickness(14, 5, 14, 5) };

        // Page 1
        private readonly TextBox _search = new() { MinWidth = 320, Padding = new Thickness(4) };
        private readonly Button _searchButton = new() { Content = "Search", Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(6, 0, 0, 0) };
        private readonly ListView _jobs = new() { Height = 280, Visibility = Visibility.Collapsed, SelectionMode = SelectionMode.Single };
        private readonly CheckBox _skip = new() { Content = "Skip this. I'll enter the address myself.", Margin = new Thickness(0, 8, 0, 0) };
        private readonly TextBlock _skipWarning = new() { Text = AddServiceService.SkipWarning, Foreground = Brushes.DarkOrange, Visibility = Visibility.Collapsed };
        // Page 2
        private readonly ListView _formsList = new() { Height = 320, SelectionMode = SelectionMode.Single };
        // Page 3
        private readonly TextBox _contact = new() { MinWidth = 260 }, _phone = new() { MinWidth = 160 }, _dateBox = new() { MinWidth = 200 },
            _attempt = new() { Width = 60 }, _poNumber = new() { MinWidth = 160 }, _other = new() { MinWidth = 320 };
        private readonly CheckBox _today = new() { Content = " Today" }, _tomorrow = new() { Content = " Tomorrow" }, _otherDate = new() { Content = " Other" },
            _reinspection = new() { Content = "" }, _poRequired = new() { Content = " Required" },
            _call = new() { Content = " Call" }, _callResults = new() { Content = " Call with results" }, _photos = new() { Content = " Take photos" };
        private DateTime _dateValue = DateTime.Now;
        private string _dateText = "";
        // Pages 1 and 3 are built once and reused: their controls can have only one parent.
        private UIElement? _page1, _page3;
        private int _formsLoad;
        private bool _adding;
        private readonly Func<string, bool> _isOpenInRed;
        // Page 4
        private readonly StackPanel _summary = new();

        public string? CreatedPath { get; private set; }
        public bool OpenRequested { get; private set; }

        // R5: every cache parsed; otherwise the caller shows "Offline Requirements Missing" and does not open the wizard.
        public bool CachesUsable => _jobsCache != null && _formsCache != null && _templates != null;

        public AddServiceWindow(CompleteProfile profile, string? rawUserType, StrandWebsiteSession session, Func<string, bool>? isOpenInRed = null)
        {
            _isOpenInRed = isOpenInRed ?? (_ => false);
            _profile = profile;
            _rawUserType = rawUserType;
            _session = session;
            _jobsCache = ReadCache(OfflineCache.Jobs);
            _formsCache = ReadCache(OfflineCache.Forms);
            _templates = ReadCache(OfflineCache.Templates);
            Title = "Add Service";
            Width = 760;
            Height = 600;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
            foreach (var b in new[] { _back, _next, _close, _open }) buttons.Children.Add(b);
            var root = new DockPanel { Margin = new Thickness(16) };
            var head = new StackPanel();
            head.Children.Add(_title);
            head.Children.Add(_description);
            DockPanel.SetDock(head, Dock.Top);
            DockPanel.SetDock(buttons, Dock.Bottom);
            DockPanel.SetDock(_error, Dock.Bottom);
            root.Children.Add(head);
            root.Children.Add(buttons);
            root.Children.Add(_error);
            root.Children.Add(_body);
            Content = root;

            _back.Click += (_, _) => { if (_adding || Quiet()) return; GoTo(_page - 1); };
            _next.Click += async (_, _) => await NextAsync();
            _close.Click += (_, _) => { if (Quiet()) return; Close(); };                          // W2 on page 4 too
            _open.Click += (_, _) => { if (Quiet()) return; OpenRequested = true; Close(); };
            BuildPages();
            GoTo(1, first: true);
            Loaded += (_, _) => _search.Focus();
        }

        private static JArray? ReadCache(OfflineCache cache)
        {
            try
            {
                using var reader = new JsonTextReader(new StringReader(File.ReadAllText(OfflineCacheService.PathOf(cache)))) { DateParseHandling = DateParseHandling.None };
                return JArray.Load(reader);
            }
            catch (Exception) { return null; }
        }

        // W2: 500 ms after every page change and every Next/Back press.
        private bool Quiet()
        {
            if (DateTime.Now < _quietUntil) return true;
            _quietUntil = DateTime.Now + Debounce;
            return false;
        }

        private static GridViewColumn Column(string header, string path, double width) =>
            new() { Header = header, Width = width, DisplayMemberBinding = new Binding(path) };

        private void BuildPages()
        {
            var jobsView = new GridView();
            foreach (var (h, p, w) in new[] { ("Address", "Address", 210.0), ("City", "City", 110.0), ("Subdivision", "Subdivision", 130.0), ("Builder", "Builder", 120.0), ("Blk/Lot", "BlockLot", 70.0), ("Job", "JobNumber", 80.0) })
                jobsView.Columns.Add(Column(h, p, w));
            _jobs.View = jobsView;
            _jobs.PreviewMouseLeftButtonUp += async (_, _) => { if (_jobs.SelectedItem != null && !Quiet()) await LeavePage1Async(fromRowClick: true); };
            _searchButton.Click += async (_, _) => await SearchAsync();
            _skip.Checked += (_, _) => { _skipWarning.Visibility = Visibility.Visible; _selectedJob = null; _jobs.SelectedItem = null; GoTo(2); };
            _skip.Unchecked += (_, _) => _skipWarning.Visibility = Visibility.Collapsed;

            var formsView = new GridView();
            foreach (var (h, p, w) in new[] { ("Name", nameof(AddFormRow.Name), 250.0), ("Last Report", nameof(AddFormRow.LastReport), 150.0), ("Last Date", nameof(AddFormRow.LastDate), 110.0), ("Last Status", nameof(AddFormRow.LastStatus), 90.0), ("Next Action", nameof(AddFormRow.NextAction), 100.0) })
                formsView.Columns.Add(Column(h, p, w));
            _formsList.View = formsView;
            _formsList.PreviewMouseLeftButtonUp += (_, _) => { if (_formsList.SelectedItem != null && !Quiet()) LeavePage2(); };

            foreach (var box in new[] { _today, _tomorrow, _otherDate })
                box.Checked += (s, _) => { foreach (var other in new[] { _today, _tomorrow, _otherDate }) if (!ReferenceEquals(other, s)) other.IsChecked = false; };
            _phone.TextChanged += (_, _) =>
            {
                string formatted = AddServiceService.FormatPhone(_phone.Text);
                if (formatted != _phone.Text) { _phone.Text = formatted; _phone.CaretIndex = formatted.Length; }
            };
            _dateBox.Text = _dateText = _dateValue.ToString("ddd, MMM dd, yyyy", CultureInfo.GetCultureInfo("en-US"));
            _dateBox.LostFocus += (_, _) => { if (_dateBox.Text != _dateText) DateEdited(); }; // only after an edit (D2)
            _page1 = Page1();
            _page3 = Page3();
            _attempt.PreviewTextInput += (_, e) => e.Handled = !e.Text.All(char.IsDigit); // whole numbers only
        }

        // D2/D3: editing the date box ticks Other and may warn.
        private void DateEdited()
        {
            if (_updatingDate) return;
            _dateText = _dateBox.Text;
            if (!DateTime.TryParse(_dateBox.Text, CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.AllowWhiteSpaces, out var picked)) return;
            _dateValue = picked;
            _otherDate.IsChecked = true;
            string? warning = AddServiceService.DateWarning(picked, DateTime.Now);
            if (warning != null) MessageBox.Show(this, warning, "", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private UIElement Page1()
        {
            var panel = new StackPanel();
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
            row.Children.Add(_search);
            row.Children.Add(_searchButton);
            panel.Children.Add(row);
            panel.Children.Add(_jobs);
            panel.Children.Add(_skip);
            panel.Children.Add(_skipWarning);
            return panel;
        }

        private UIElement Page3()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            void Row(string label, UIElement editor)
            {
                int r = grid.RowDefinitions.Count;
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var l = new TextBlock { Text = label, Margin = new Thickness(0, 6, 10, 0), VerticalAlignment = VerticalAlignment.Top };
                Grid.SetRow(l, r); grid.Children.Add(l);
                if (editor is FrameworkElement fe) fe.Margin = new Thickness(0, 3, 0, 3);
                Grid.SetRow(editor, r); Grid.SetColumn(editor, 1); grid.Children.Add(editor);
            }
            StackPanel H(params UIElement[] items) { var p = new StackPanel { Orientation = Orientation.Horizontal }; foreach (var i in items) { if (i is FrameworkElement f) f.Margin = new Thickness(0, 0, 10, 0); p.Children.Add(i); } return p; }
            Row("Contact Name:", _contact);
            Row("Contact Phone:", _phone);
            Row("Date:", H(_today, _tomorrow, _otherDate, _dateBox));
            Row("Re-inspection:", H(_reinspection, new TextBlock { Text = "Attempt:", VerticalAlignment = VerticalAlignment.Center }, _attempt));
            Row("Purchase Order:", H(_poRequired, new TextBlock { Text = "PO Number:", VerticalAlignment = VerticalAlignment.Center }, _poNumber));
            Row("Requests:", H(_call, _callResults, _photos));
            Row("Other:", _other);
            return grid;
        }

        private void GoTo(int page, bool first = false)
        {
            if (page < 1 || page > 4) return;
            if (!first) _pageChanged = true;
            _page = page;
            _quietUntil = DateTime.Now + Debounce; // W2
            _error.Text = "";
            switch (page)
            {
                case 1:
                    _title.Text = "What is the address?";
                    _description.Text = "Search for the address using the street name or job number.";
                    _body.Content = _page1;
                    break;
                case 2:
                    _title.Text = "What inspection will you perform?";
                    _description.Text = "";
                    _body.Content = _formsList;
                    _ = LoadFormsAsync();
                    break;
                case 3:
                    _title.Text = "Additional information about the service";
                    _description.Text = "";
                    _body.Content = _page3;
                    break;
                case 4:
                    _title.Text = "The inspection has been created";
                    _description.Text = "";
                    _body.Content = _summary;
                    break;
            }
            // W1 captions and buttons.
            _back.Visibility = page is 2 or 3 ? Visibility.Visible : Visibility.Collapsed;
            _next.Visibility = page <= 3 ? Visibility.Visible : Visibility.Collapsed;
            _next.Content = page == 3 ? "Add" : _pageChanged ? "Next" : "Next >";
            _close.Visibility = _open.Visibility = page == 4 ? Visibility.Visible : Visibility.Collapsed;
        }

        private async Task NextAsync()
        {
            if (Quiet()) return; // W2: a debounced press does nothing
            switch (_page)
            {
                case 1: await LeavePage1Async(fromRowClick: false); break;
                case 2: LeavePage2(); break;
                case 3: await AddAsync(); break;
            }
        }

        // P1: Enter/Next in the search box searches; otherwise exactly one row must be selected.
        private async Task LeavePage1Async(bool fromRowClick)
        {
            if (_skip.IsChecked == true) { GoTo(2); return; }
            if (!fromRowClick && _search.IsKeyboardFocusWithin) { await SearchAsync(); return; }
            if (_jobs.SelectedItem is not JobRow row) { _error.Text = AddServiceService.SelectAddress; return; }
            if (string.IsNullOrWhiteSpace(row.Job["JobNumber"]?.ToString())) return; // a blank job number stays on page 1
            _selectedJob = row.Job;
            GoTo(2);
        }

        private sealed record JobRow(JObject Job, string Address, string City, string Subdivision, string Builder, string BlockLot, string JobNumber);

        private async Task SearchAsync()
        {
            string query = _search.Text;
            if (string.IsNullOrWhiteSpace(query)) { _jobs.ItemsSource = null; return; } // searching stays possible (RED deviation)
            _searchButton.IsEnabled = false;
            List<JObject>? found;
            try { found = await AddServiceService.SearchOnlineAsync(_session, query); }
            finally { _searchButton.IsEnabled = true; }
            if (found == null)
            {
                MessageBox.Show(this, AddServiceService.OfflineSearchText + Environment.NewLine + Environment.NewLine + AddServiceService.OfflineSearchText2,
                    AddServiceService.OfflineSearchCaption, MessageBoxButton.OK, MessageBoxImage.Information);
                found = AddServiceService.SearchOffline(_jobsCache, query);
            }
            if (found.Count == 0)
                MessageBox.Show(this, AddServiceService.NotFoundText(query), AddServiceService.NotFoundCaption, MessageBoxButton.OK, MessageBoxImage.Warning);
            _jobs.ItemsSource = found.Select(j => new JobRow(j, Str(j["Address"]), Str(j["City"]), Str(j["Subdivision"]), Str(j["BuilderName"]),
                AddServiceService.BlockLot(j), Str(j["JobNumber"]))).ToList();
            _jobs.Visibility = Visibility.Visible;
        }

        private static string Str(JToken? t) => t == null || t.Type == JTokenType.Null ? "" : t.ToString();

        private async Task LoadFormsAsync()
        {
            int load = ++_formsLoad;
            var job = _selectedJob;
            string? job7 = job == null ? null : AddServiceService.Job7(job);
            _formsList.ItemsSource = null;
            var forms = await AddServiceService.FormsAsync(job == null ? null : _session, job7, _formsCache);
            string folder = _profile.Folder;
            await Task.Run(() => AddServiceService.ApplyHistory(forms, folder, job7, includeReview: ReviewAccess.CountReviewFiles(folder) > 0));
            if (load != _formsLoad || !ReferenceEquals(job, _selectedJob) || _page != 2)
                return; // the user moved on: this answer belongs to another job
            _forms = forms;
            _formsList.ItemsSource = forms;
        }

        private void LeavePage2()
        {
            if (_formsList.SelectedItem is not AddFormRow form) { _error.Text = AddServiceService.SelectInspection; return; }
            _selectedForm = form;
            // D1 defaults, every time page 2 is left forward.
            var (contact, phone, other) = AddServiceService.NhoDefaults(_selectedJob, form.ServiceId);
            _contact.Text = contact;
            _phone.Text = phone;
            _other.Text = other;
            _reinspection.IsChecked = form.Attempt > 1;
            _attempt.Text = (_skip.IsChecked == true ? 1 : form.Attempt).ToString(CultureInfo.InvariantCulture);
            _poRequired.IsChecked = AddServiceService.DefaultPoRequired(form);
            GoTo(3);
        }

        // P3 "Add": build, duplicates, save; at most once per wizard (W2 RED deviation).
        private async Task AddAsync()
        {
            if (_added || _selectedForm == null) return;
            DateTime now = DateTime.Now;
            DateTime? date = _today.IsChecked == true ? now : _tomorrow.IsChecked == true ? now.AddDays(1) : _otherDate.IsChecked == true ? _dateValue : null; // D6
            var request = new AddRequest
            {
                Job = _skip.IsChecked == true ? null : _selectedJob,
                Form = _selectedForm,
                Contact = _contact.Text,
                ContactPhone = _phone.Text,
                Date = date,
                Reinspection = _reinspection.IsChecked == true,
                AttemptText = _attempt.Text,
                PurchaseOrderRequired = _poRequired.IsChecked == true,
                PurchaseOrderNumber = _poNumber.Text,
                Requests = AddServiceService.RequestsText(_call.IsChecked == true, _callResults.IsChecked == true, _photos.IsChecked == true, _other.Text)
            };
            string jobText = request.Skip ? AddServiceService.ManualJobNumber : Str(_selectedJob!["JobNumber"]);
            _next.IsEnabled = false;
            _adding = true;
            _back.IsEnabled = false;
            try
            {
                var doc = await AddServiceService.BuildAsync(request, _profile, _rawUserType, _templates, _session, now);
                string number = doc["InspectionNumber"]!.ToString();
                // S1: ask every question first; any No refuses; all Yes deletes, then saves.
                var duplicates = AddServiceService.FindDuplicates(_profile.Folder, number);
                // As ORDERS Add to My List (OC §10.10): never delete a copy that is open in a RED editor.
                if (duplicates.Any(d => _isOpenInRed(d.Path)))
                {
                    _error.Text = $"Cannot add. '{number}' is open in RED. Close it first, then try again.";
                    return;
                }
                foreach (var duplicate in duplicates)
                {
                    if (MessageBox.Show(this, AddServiceService.DuplicateQuestion(duplicate.Number, duplicate.FolderCaption), "", MessageBoxButton.YesNo, MessageBoxImage.Error) != MessageBoxResult.Yes)
                    {
                        MessageBox.Show(this, AddServiceService.DuplicateRefused, "", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }
                }
                foreach (var duplicate in duplicates)
                    File.Delete(duplicate.Path);
                CreatedPath = AddServiceService.Save(_profile.Folder, doc);
                _added = true;
                // S3: RequestFiles once, only for a selected job and UserType exactly "Inspector"; the result is ignored.
                if (AddServiceService.ShouldRequestFiles(request, _rawUserType))
                    _ = JobFilesService.RequestFilesAsync(_session, jobText, default).ContinueWith(t =>
                        DiagnosticLogService.Info(t.Status == TaskStatus.RanToCompletion && t.Result == JobFilesService.RequestedMessage
                            ? "Add: job files requested." : "Add: the job files request failed."), TaskScheduler.Default);
                ShowSummary(doc);
                GoTo(4);
            }
            catch (InvalidOperationException ex)
            {
                _error.Text = ex.Message; // "Cannot find template for form id: {id}."
            }
            catch (Exception ex)
            {
                // S5: RED's log gets the exception type only (file errors carry the report's path and job number).
                DiagnosticLogService.Info($"Add Service failed: {ex.GetType().Name}");
                _error.Text = AddServiceService.AddFailed(_selectedForm.Name, jobText) +
                              (ex is IOException or UnauthorizedAccessException ? " The file could not be written in My List." : " " + ex.Message);
            }
            finally
            {
                _adding = false;
                _back.IsEnabled = true;
                _next.IsEnabled = !_added;
            }
        }

        private void ShowSummary(JObject doc)
        {
            _summary.Children.Clear();
            void Line(string label, string? value) => _summary.Children.Add(new TextBlock { Text = $"{label} {value}", Margin = new Thickness(0, 3, 0, 3) });
            Line("Inspection", Str(doc["InspectionName"]));
            Line("Report Number", Str(doc["InspectionNumber"]));
            Line("Strand Job", Str(doc["JobNumber"]));
            Line("Project:", Str(doc["Project"]));
            Line("Builder:", Str(doc["BuilderName"]));
            Line("Address", Str(doc["Address"]));
            Line("City/State", $"{Str(doc["City"])} {Str(doc["State"])}".Trim());
            Line("Lot/Block:", $"{Str(doc["Lot"])}/{Str(doc["Block"])}");
        }
    }
}
