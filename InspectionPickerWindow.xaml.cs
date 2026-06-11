using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Win32;

namespace InspectionEditor
{
    public partial class InspectionPickerWindow : Window
    {
        public event EventHandler<string>? InspectionOpenRequested;

        private List<InspectionFileInfo> _allInspections = new List<InspectionFileInfo>();
        private GridViewColumnHeader? _lastHeaderClicked = null;
        private ListSortDirection _lastDirection = ListSortDirection.Ascending;
        private string? _sortColumnPath = "Builder"; // Default sort column

        private static readonly Dictionary<string, string> _headerSortPaths = new()
        {
            { "Address",  "Address" },
            { "Type",     "InspectionTypeName" },
            { "Date",     "Date" },
            { "Builder",  "Builder" },
            { "Attempt#", "AttemptNumberSort" },
            { "Signed",   "EditStatus" },
        };
        private string _currentFolderPath = "";
        private PickerSettings? _settings;
        private readonly bool _stayOpenHome;
        
        // Mouse-based scrolling (tablet reports touch as mouse events, not WPF Touch events)
        private Point? _listScrollStartPoint;
        private double _listScrollStartVerticalOffset;
        private double _listScrollStartHorizontalOffset;
        private bool _listIsScrolling = false;
        private bool _listScrollStarted = false;
        private InspectionFileInfo? _pendingTapInspection;
        private int _selectionOpenRequestId = 0;
        private double _listFontSize = 16;
        private bool _isLoadingSettingsUi = false;
        private bool _searchBarVisible = true;
        private string _categoryMode = "None";
        private bool _isApplyingColumnSettings = false;
        private readonly Dictionary<string, GridViewColumn> _columnsByKey = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, CheckBox> _columnCheckBoxesByKey = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, double> _defaultColumnWidthsByKey = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _columnOrder = new();
        private bool _hasAppliedColumnSettingsOnce = false;
        private readonly System.Windows.Threading.DispatcherTimer _columnWidthSaveTimer;
        private int _ordersLoadVersion = 0;
        private readonly HashSet<string> _openingFilePaths = new(StringComparer.OrdinalIgnoreCase);
        private int _aboutLogoClickCount = 0;
        private DateTime _lastAboutLogoClick = DateTime.MinValue;
        private bool _aboutTaglineVisible = false;
        
        // Settings file path
        private static string SettingsFilePath => Path.Combine(AppIdentity.LocalAppDataPath, "inspection_picker_settings.json");

        private static string LegacySettingsFilePath => Path.Combine(
            Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location) ?? ".",
            "inspection_picker_settings.json");

        private static string EditorPreferencesFilePath => Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory,
            "preferences.txt");

        private static string InlineDrawerPreferencesFilePath => Path.Combine(
            AppIdentity.LocalAppDataPath,
            "inline-drawer-preferences.json");

        private static string RedAppSettingsFilePath => Path.Combine(
            AppIdentity.LocalAppDataPath,
            "red_app_settings.json");
        
        public string? SelectedFilePath { get; private set; }
        
        public InspectionPickerWindow(bool stayOpenHome = false)
        {
            _stayOpenHome = stayOpenHome;
            _columnWidthSaveTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(450)
            };
            _columnWidthSaveTimer.Tick += (_, _) =>
            {
                _columnWidthSaveTimer.Stop();
                if (_isApplyingColumnSettings)
                    return;

                CaptureVisibleColumnWidths();
                SaveSettings();
            };
            InitializeComponent();
            InitializeColumnLayout();
            WatchColumnWidthChanges();
            Loaded += InspectionPickerWindow_Loaded;
            if (_stayOpenHome)
            {
                Title = $"{AppIdentity.DisplayName} - My List";
                OpenButton.Content = "Open";
                CancelButton.Content = "Close";
                StatusText.Text = "Tap an inspection once to open it";
            }
        }

        public InspectionPickerWindow(string defaultFolderPath, bool stayOpenHome = false) : this(stayOpenHome)
        {
            _settings = LoadSettings();
            string? savedPath = _settings?.LastFolderPath;
            if (savedPath != null && savedPath.Contains("archive", StringComparison.OrdinalIgnoreCase))
                savedPath = null;
            if (savedPath != null && !Directory.Exists(savedPath))
                savedPath = null;
            LoadInspections(savedPath ?? defaultFolderPath);
        }

        private void InspectionPickerWindow_Loaded(object sender, RoutedEventArgs e)
        {
            bool canRestoreLayout = _settings?.LayoutVersion >= 2;

            RestoreWindowPlacement(canRestoreLayout);

            if (canRestoreLayout && _settings?.ListFontSize >= 13 && _settings.ListFontSize <= 24)
                _listFontSize = _settings.ListFontSize;

            _searchBarVisible = _settings?.SearchBarVisible ?? true;
            _categoryMode = NormalizeCategoryMode(_settings?.CategoryMode);
            ApplyListFontSize();

            // Restore column widths
            if (canRestoreLayout && _settings?.ColumnWidths?.Length > 0)
                ApplyColumnWidths(_settings.ColumnWidths);
            ApplyColumnSettings();

            LoadSettingsTabValues();
            ShowMyListTab();

            // Restore sort state — RefreshList (called later) will pick it up
            if (!string.IsNullOrEmpty(_settings?.SortColumn))
            {
                _sortColumnPath = _settings.SortColumn == "FileName"
                    ? "AttemptNumberSort"
                    : _settings.SortColumn;
                _lastDirection  = _settings.SortDirection == "Descending"
                    ? ListSortDirection.Descending
                    : ListSortDirection.Ascending;
            }

            // Focus search box so no list item appears pre-selected (avoids false color indication)
            ApplySearchBarVisibility();
            if (_searchBarVisible)
                SearchBox.Focus();
            if (_stayOpenHome)
                Activate();
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_stayOpenHome && TryFocusOpenInspectionBeforeClosing())
            {
                e.Cancel = true;
                return;
            }

            CaptureVisibleColumnWidths();
            SaveSettings();
        }

        private bool TryFocusOpenInspectionBeforeClosing()
        {
            var openInspection = Application.Current.Windows
                .OfType<MainWindow>()
                .FirstOrDefault(w => w.HasOpenInspection);

            if (openInspection == null)
                return false;

            openInspection.BringInspectionToFront();

            MessageBox.Show(
                openInspection,
                "INS file still open.\n\nClose the open INS file before closing this screen.",
                "INS file still open",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);

            openInspection.Dispatcher.BeginInvoke(new Action(openInspection.BringInspectionToFront),
                System.Windows.Threading.DispatcherPriority.ApplicationIdle);

            return true;
        }
        
        private PickerSettings? LoadSettings()
        {
            try
            {
                if (File.Exists(SettingsFilePath))
                    return JsonSerializer.Deserialize<PickerSettings>(File.ReadAllText(SettingsFilePath));
                if (File.Exists(LegacySettingsFilePath))
                    return JsonSerializer.Deserialize<PickerSettings>(File.ReadAllText(LegacySettingsFilePath));
            }
            catch { }
            return null;
        }

        private void SaveSettings()
        {
            try
            {
                var s = new PickerSettings
                {
                    LayoutVersion = 4,
                    LastFolderPath = _currentFolderPath,
                    ColumnWidths   = GetColumnWidths(),
                    ColumnWidthsByKey = GetColumnWidthsByKey(),
                    ColumnVisibility = GetColumnVisibilityByKey(),
                    SortColumn     = _sortColumnPath ?? "",
                    SortDirection  = _lastDirection.ToString(),
                    WindowLeft     = WindowState == WindowState.Normal ? Left : RestoreBounds.Left,
                    WindowTop      = WindowState == WindowState.Normal ? Top : RestoreBounds.Top,
                    WindowWidth    = WindowState == WindowState.Normal ? Width : RestoreBounds.Width,
                    WindowHeight   = WindowState == WindowState.Normal ? Height : RestoreBounds.Height,
                    WindowState    = WindowState.ToString(),
                    ListFontSize   = _listFontSize,
                    SearchBarVisible = _searchBarVisible,
                    CategoryMode = _categoryMode
                };
                Directory.CreateDirectory(Path.GetDirectoryName(SettingsFilePath) ?? ".");
                File.WriteAllText(SettingsFilePath,
                    JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
                _settings = s;
            }
            catch { }
        }

        private void RestoreWindowPlacement(bool canRestoreLayout)
        {
            if (!canRestoreLayout || _settings == null)
                return;

            bool hasPlacement = _settings.WindowWidth >= MinWidth &&
                                _settings.WindowHeight >= MinHeight &&
                                IsReasonableWindowPosition(_settings.WindowLeft, _settings.WindowTop);

            if (hasPlacement)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = _settings.WindowLeft;
                Top = _settings.WindowTop;
                Width = _settings.WindowWidth;
                Height = _settings.WindowHeight;
            }
            else if (_settings.WindowWidth >= MinWidth && _settings.WindowHeight >= MinHeight)
            {
                Width = _settings.WindowWidth;
                Height = _settings.WindowHeight;
            }

            if (Enum.TryParse(_settings.WindowState, out WindowState savedState) &&
                savedState != WindowState.Minimized)
            {
                WindowState = savedState;
            }
        }

        private static bool IsReasonableWindowPosition(double left, double top)
        {
            return left > SystemParameters.VirtualScreenLeft - 2000 &&
                   top > SystemParameters.VirtualScreenTop - 2000 &&
                   left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth + 2000 &&
                   top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight + 2000;
        }

        private void LoadSettingsTabValues()
        {
            _isLoadingSettingsUi = true;
            try
            {
                CustomNotifyMessageBox.Text = NotifyQRWindow.LoadSavedCustomMessage();
                SetCategorizeComboBoxSelection();
                CustomNotifySavedText.Visibility = Visibility.Collapsed;
            }
            catch { }
            finally
            {
                _isLoadingSettingsUi = false;
            }
        }

        private void ApplyListFontSize()
        {
            _listFontSize = Math.Max(13, Math.Min(24, _listFontSize));
            InspectionListView.FontSize = _listFontSize;
            OrdersListView.FontSize = _listFontSize;
            SearchBox.FontSize = Math.Max(16, _listFontSize + 1);
            StatusText.FontSize = Math.Max(14, _listFontSize - 1);
            FontSizeSettingText.Text = $"{_listFontSize:0} pt";
        }

        private void IncreaseFontButton_Click(object sender, RoutedEventArgs e)
        {
            _listFontSize += 1;
            ApplyListFontSize();
            SaveSettings();
        }

        private void DecreaseFontButton_Click(object sender, RoutedEventArgs e)
        {
            _listFontSize -= 1;
            ApplyListFontSize();
            SaveSettings();
        }

        private void ResetFontButton_Click(object sender, RoutedEventArgs e)
        {
            _listFontSize = 16;
            ApplyListFontSize();
            SaveSettings();
        }

        private void ShowSearchButton_Click(object sender, RoutedEventArgs e)
        {
            _searchBarVisible = true;
            ApplySearchBarVisibility();
            SearchBox.Focus();
            SaveSettings();
        }

        private void HideSearchButton_Click(object sender, RoutedEventArgs e)
        {
            _searchBarVisible = false;
            if (!string.IsNullOrEmpty(SearchBox.Text))
                SearchBox.Text = "";
            ApplySearchBarVisibility();
            SaveSettings();
        }

        private void ApplySearchBarVisibility()
        {
            SearchBarBorder.Visibility = _searchBarVisible ? Visibility.Visible : Visibility.Collapsed;
            ShowSearchButton.Visibility = _searchBarVisible ? Visibility.Collapsed : Visibility.Visible;
        }

        private void CategorizeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isLoadingSettingsUi || !IsLoaded)
                return;

            _categoryMode = NormalizeCategoryMode((CategorizeComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString());
            ApplyColumnSettings();
            RefreshList(SearchBox.Text);
            SaveSettings();
        }

        private void SetCategorizeComboBoxSelection()
        {
            foreach (var item in CategorizeComboBox.Items.OfType<ComboBoxItem>())
            {
                if (NormalizeCategoryMode(item.Tag?.ToString()) == _categoryMode)
                {
                    CategorizeComboBox.SelectedItem = item;
                    break;
                }
            }
        }

        private static string NormalizeCategoryMode(string? mode)
        {
            return mode switch
            {
                "Builder" => "Builder",
                "Subdivision" => "Subdivision",
                "JobType" => "JobType",
                _ => "None"
            };
        }

        private bool ShouldSuppressColumnForCategory(string key)
        {
            return (_categoryMode == "Builder" && key == "Builder") ||
                   (_categoryMode == "JobType" && key == "Type");
        }

        private void MyListTabButton_Click(object sender, RoutedEventArgs e) => ShowMyListTab();

        private void OrdersTabButton_Click(object sender, RoutedEventArgs e) => ShowOrdersTab();

        private void SettingsTabButton_Click(object sender, RoutedEventArgs e) => ShowSettingsTab();

        private void AboutTabButton_Click(object sender, RoutedEventArgs e) => ShowAboutTab();

        private void ShowMyListTab()
        {
            MyListPanel.Visibility = Visibility.Visible;
            SettingsPanel.Visibility = Visibility.Collapsed;
            OrdersPanel.Visibility = Visibility.Collapsed;
            AboutPanel.Visibility = Visibility.Collapsed;
            SetActiveTab(MyListTabButton);
        }

        private void ShowOrdersTab()
        {
            MyListPanel.Visibility = Visibility.Collapsed;
            SettingsPanel.Visibility = Visibility.Collapsed;
            OrdersPanel.Visibility = Visibility.Visible;
            AboutPanel.Visibility = Visibility.Collapsed;
            ShowOrdersNotConnectedMessage();
            SetActiveTab(OrdersTabButton);
        }

        private void RefreshOrdersButton_Click(object sender, RoutedEventArgs e)
        {
            ShowOrdersNotConnectedMessage();
        }

        private void ShowOrdersNotConnectedMessage()
        {
            OrdersListView.ItemsSource = null;
            OrdersStatusText.Text = "Live Dallas orders feed is not wired yet. The Inspect2022 offline job cache is intentionally hidden because it does not match the live Schedule tab.";
        }

        private async Task LoadOrdersFromInspectCacheAsync()
        {
            int loadVersion = ++_ordersLoadVersion;
            RefreshOrdersButton.IsEnabled = false;
            OrdersStatusText.Text = "Loading Inspect2022 order cache...";

            try
            {
                string? cachePath = FindNewestInspectOfflineJobsPath();
                if (string.IsNullOrWhiteSpace(cachePath) || !File.Exists(cachePath))
                {
                    OrdersListView.ItemsSource = null;
                    OrdersStatusText.Text = "Inspect2022 order cache not found yet.";
                    return;
                }

                var loadResult = await Task.Run(() => LoadOrdersFromCacheFile(cachePath));
                if (loadVersion != _ordersLoadVersion)
                    return;

                OrdersListView.ItemsSource = loadResult.Rows;
                var view = CollectionViewSource.GetDefaultView(OrdersListView.ItemsSource);
                view.GroupDescriptions.Clear();
                view.SortDescriptions.Clear();
                view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(OrderFileInfo.StatusGroup)));
                view.SortDescriptions.Add(new SortDescription(nameof(OrderFileInfo.StatusGroup), ListSortDirection.Descending));
                view.SortDescriptions.Add(new SortDescription(nameof(OrderFileInfo.ScheduleDate), ListSortDirection.Ascending));
                view.Refresh();

                string updated = File.GetLastWriteTime(cachePath).ToString("M/d/yyyy h:mm tt");
                string capped = loadResult.WasCapped ? " Showing newest 300." : "";
                OrdersStatusText.Text = $"Loaded {loadResult.Rows.Count} cached scheduled/request jobs from Inspect2022. Cache updated {updated}.{capped}";
            }
            catch (Exception ex)
            {
                OrdersListView.ItemsSource = null;
                OrdersStatusText.Text = $"Could not load Inspect2022 order cache: {ex.Message}";
            }
            finally
            {
                RefreshOrdersButton.IsEnabled = true;
            }
        }

        private static (List<OrderFileInfo> Rows, bool WasCapped) LoadOrdersFromCacheFile(string cachePath)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(cachePath));
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return (new List<OrderFileInfo>(), false);

            var recentFloor = DateTime.Today.AddDays(-14);
            var rows = new List<OrderFileInfo>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                string jobNumber = FirstStringProperty(item, "JobNumber");
                string address = FirstStringProperty(item, "Address");
                string subdivision = FirstStringProperty(item, "Subdivision");
                string builder = FirstStringProperty(item, "BuilderName");
                string city = FirstStringProperty(item, "City");
                string requests = FirstStringProperty(item, "Requests");
                string superintendent = FirstStringProperty(item, "Superintendent");
                string scheduleRaw = FirstStringProperty(item, "ScheduleDate");

                DateTime? scheduleDate = null;
                if (DateTime.TryParse(scheduleRaw, out var parsedSchedule))
                    scheduleDate = parsedSchedule;

                bool hasRecentSchedule = scheduleDate.HasValue && scheduleDate.Value.Date >= recentFloor;
                bool hasRequestNote = !string.IsNullOrWhiteSpace(requests) &&
                                      (!scheduleDate.HasValue || scheduleDate.Value.Date >= recentFloor);
                if (!hasRecentSchedule && !hasRequestNote)
                    continue;

                rows.Add(new OrderFileInfo
                {
                    StatusGroup = string.IsNullOrWhiteSpace(superintendent) ? "Unassigned" : "Assigned",
                    JobNumber = jobNumber,
                    Address = address,
                    Subdivision = subdivision,
                    Builder = builder,
                    City = city,
                    Service = "",
                    ScheduleDate = scheduleDate,
                    Assigned = superintendent,
                    Note = requests
                });
            }

            rows = rows
                .OrderBy(r => r.StatusGroup == "Unassigned" ? 0 : 1)
                .ThenBy(r => r.ScheduleDate ?? DateTime.MaxValue)
                .ThenBy(r => r.Builder)
                .ThenBy(r => r.Address)
                .ToList();

            bool wasCapped = rows.Count > 300;
            if (wasCapped)
                rows = rows.Take(300).ToList();

            return (rows, wasCapped);
        }

        private static string? FindNewestInspectOfflineJobsPath()
        {
            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Strand",
                "Inspect");

            if (!Directory.Exists(root))
                return null;

            return Directory.GetFiles(root, "OfflineJobs.json", SearchOption.AllDirectories)
                .OrderByDescending(File.GetLastWriteTime)
                .FirstOrDefault();
        }

        private void ShowSettingsTab()
        {
            MyListPanel.Visibility = Visibility.Collapsed;
            SettingsPanel.Visibility = Visibility.Visible;
            OrdersPanel.Visibility = Visibility.Collapsed;
            AboutPanel.Visibility = Visibility.Collapsed;
            SetActiveTab(SettingsTabButton);
        }

        private void ShowAboutTab()
        {
            MyListPanel.Visibility = Visibility.Collapsed;
            SettingsPanel.Visibility = Visibility.Collapsed;
            OrdersPanel.Visibility = Visibility.Collapsed;
            AboutPanel.Visibility = Visibility.Visible;
            SetActiveTab(AboutTabButton);
        }

        private void SetActiveTab(Button activeButton)
        {
            var inactiveForeground = new SolidColorBrush(Color.FromRgb(31, 41, 51));
            var inactiveBackground = new SolidColorBrush(Color.FromRgb(232, 236, 239));
            var inactiveBorder = new SolidColorBrush(Color.FromRgb(196, 205, 213));
            foreach (var button in new[] { MyListTabButton, OrdersTabButton, SettingsTabButton, AboutTabButton })
            {
                button.Background = inactiveBackground;
                button.BorderBrush = inactiveBorder;
                button.Foreground = inactiveForeground;
            }

            activeButton.Background = Brushes.White;
            activeButton.BorderBrush = new SolidColorBrush(Color.FromRgb(153, 0, 0));
            activeButton.Foreground = new SolidColorBrush(Color.FromRgb(153, 0, 0));
        }

        public void NotifyInspectionWindowClosed()
        {
            _openingFilePaths.Clear();
            if (!string.IsNullOrWhiteSpace(_currentFolderPath) && Directory.Exists(_currentFolderPath))
                LoadInspections(_currentFolderPath);

            ShowMyListTab();
            BringToFront();
        }

        private void BringToFront()
        {
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;

            Activate();
            Topmost = true;
            Topmost = false;
            Focus();
        }

        private void AboutLogo_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            var now = DateTime.Now;
            if ((now - _lastAboutLogoClick).TotalMilliseconds > 500)
                _aboutLogoClickCount = 0;

            _aboutLogoClickCount++;
            _lastAboutLogoClick = now;

            if (_aboutLogoClickCount >= 3)
            {
                _aboutLogoClickCount = 0;
                ToggleAboutTagline();
                ForceAboutUpdateAsync();
            }
        }

        private void ToggleAboutTagline()
        {
            if (_aboutTaglineVisible)
                HideAboutTagline();
            else
                ShowAboutTagline();
        }

        private void ShowAboutTagline()
        {
            _aboutTaglineVisible = true;
            AboutTagline.Visibility = Visibility.Visible;

            var fadeIn = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(300),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            var slideDown = new DoubleAnimation
            {
                From = -20,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(300),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            AboutTagline.BeginAnimation(UIElement.OpacityProperty, fadeIn);
            AboutTaglineTranslate.BeginAnimation(TranslateTransform.YProperty, slideDown);
        }

        private void HideAboutTagline()
        {
            _aboutTaglineVisible = false;

            var fadeOut = new DoubleAnimation
            {
                From = 1,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(200),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };

            var slideUp = new DoubleAnimation
            {
                From = 0,
                To = -20,
                Duration = TimeSpan.FromMilliseconds(200),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
            };

            fadeOut.Completed += (s, e) => AboutTagline.Visibility = Visibility.Collapsed;

            AboutTagline.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            AboutTaglineTranslate.BeginAnimation(TranslateTransform.YProperty, slideUp);
        }

        private async void ForceAboutUpdateAsync()
        {
            AboutUpdatePanel.Visibility = Visibility.Visible;
            AboutUpdateResultsGrid.Visibility = Visibility.Collapsed;
            AboutUpdateStatusText.Visibility = Visibility.Visible;
            AboutUpdateProgressBar.IsIndeterminate = true;
            AboutUpdateProgressBar.Value = 0;
            AboutUpdateStatusText.Text = AppIdentity.IsDevBuild
                ? "RED 2.0 Dev skips app self-updates; refreshing datasets only..."
                : "Checking for updates...";

            InspectionEditor.Services.StatsUpdateResult? statsResult = null;
            InspectionEditor.Services.StatsUpdateResult? teamStatsResult = null;
            try
            {
                statsResult = await InspectionEditor.Services.DataUpdateService.ForceUpdateStatsAsync();
                teamStatsResult = new InspectionEditor.Services.StatsUpdateResult
                {
                    CurrentDate = statsResult.CurrentDate,
                    LatestDate = statsResult.LatestDate,
                    Updated = statsResult.Updated,
                    Error = statsResult.Error
                };
            }
            catch (Exception ex)
            {
                statsResult = new InspectionEditor.Services.StatsUpdateResult
                {
                    CurrentDate = InspectionEditor.Services.DataUpdateService.GetLocalStatsDate(),
                    LatestDate = "",
                    Updated = false,
                    Error = ex.Message
                };
                teamStatsResult = statsResult;
            }

            AboutUpdateProgressBar.IsIndeterminate = false;
            AboutUpdateProgressBar.Value = 100;
            AboutUpdateStatusText.Visibility = Visibility.Collapsed;
            AboutUpdateResultsGrid.Visibility = Visibility.Visible;

            AboutRedHadText.Text = $"v{AppIdentity.Version}";
            AboutRedNowText.Text = $"v{AppIdentity.Version}";
            AboutRedStatusText.Text = AppIdentity.IsDevBuild
                ? "Dev build: app self-update skipped"
                : "Manual check available from classic editor";
            AboutRedStatusText.Foreground = new SolidColorBrush(Color.FromRgb(46, 125, 50));

            PopulateAboutStatsRow(
                statsResult,
                AboutStatsHadText,
                AboutStatsNowText,
                AboutStatsStatusText,
                InspectionEditor.Services.DataUpdateService.GetLocalStatsDate());

            PopulateAboutStatsRow(
                teamStatsResult,
                AboutTeamStatsHadText,
                AboutTeamStatsNowText,
                AboutTeamStatsStatusText,
                InspectionEditor.Services.DataUpdateService.GetLocalTeamStatsDate());
        }

        private static void PopulateAboutStatsRow(
            InspectionEditor.Services.StatsUpdateResult? result,
            TextBlock hadText,
            TextBlock nowText,
            TextBlock statusText,
            string fallbackDate)
        {
            if (result == null)
            {
                hadText.Text = fallbackDate;
                nowText.Text = "-";
                statusText.Text = "Check failed";
                statusText.Foreground = new SolidColorBrush(Color.FromRgb(180, 0, 0));
                return;
            }

            hadText.Text = result.CurrentDate;
            nowText.Text = string.IsNullOrWhiteSpace(result.LatestDate) ? "-" : result.LatestDate;
            if (result.Error != null)
            {
                statusText.Text = $"Issue: {result.Error}";
                statusText.Foreground = new SolidColorBrush(Color.FromRgb(180, 0, 0));
            }
            else if (result.Updated)
            {
                statusText.Text = "Updated";
                statusText.Foreground = new SolidColorBrush(Color.FromRgb(21, 101, 192));
            }
            else
            {
                statusText.Text = "Up to date";
                statusText.Foreground = new SolidColorBrush(Color.FromRgb(46, 125, 50));
            }
        }

        private void CustomNotifyMessageBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_isLoadingSettingsUi)
                CustomNotifySavedText.Visibility = Visibility.Collapsed;
        }

        private void SaveCustomNotifyMessageButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                NotifyQRWindow.SaveCustomMessage(CustomNotifyMessageBox.Text ?? "");
                CustomNotifySavedText.Visibility = Visibility.Visible;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Could not save message: {ex.Message}", "Settings", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ClearCustomNotifyMessageButton_Click(object sender, RoutedEventArgs e)
        {
            CustomNotifyMessageBox.Text = "";
            CustomNotifySavedText.Visibility = Visibility.Collapsed;
        }

        private void ResetAppPreferencesButton_Click(object sender, RoutedEventArgs e)
        {
            var openInspection = Application.Current.Windows
                .OfType<MainWindow>()
                .FirstOrDefault(w => w.HasOpenInspection);

            if (openInspection != null)
            {
                MessageBox.Show(
                    this,
                    "Close any open INS editor windows before resetting RED preferences.",
                    "Close INS first",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            var result = MessageBox.Show(
                this,
                "Reset saved RED window sizes, column layout, font settings, and remembered inline tools back to the post-install defaults?",
                "Reset RED Preferences",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result != MessageBoxResult.Yes)
                return;

            DeleteIfExists(SettingsFilePath);
            DeleteIfExists(LegacySettingsFilePath);
            DeleteIfExists(EditorPreferencesFilePath);
            DeleteIfExists(InlineDrawerPreferencesFilePath);
            DeleteIfExists(RedAppSettingsFilePath);

            _settings = new PickerSettings
            {
                LayoutVersion = 4,
                LastFolderPath = _currentFolderPath
            };
            _listFontSize = 16;
            _searchBarVisible = true;
            _categoryMode = "None";
            _sortColumnPath = "Builder";
            _lastDirection = ListSortDirection.Ascending;
            WindowState = WindowState.Normal;
            Width = 1400;
            Height = 900;

            CenterOnCurrentScreen();
            SetCategorizeComboBoxSelection();
            ApplyListFontSize();
            ApplySearchBarVisibility();
            ApplyColumnSettings();
            RefreshList(SearchBox.Text);
            SaveSettings();

            MessageBox.Show(
                this,
                "RED preferences were reset. Newly opened INS editor windows will use the clean default layout and tools.",
                "Preferences Reset",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }

        private static void DeleteIfExists(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch { }
        }

        private void CenterOnCurrentScreen()
        {
            Left = SystemParameters.WorkArea.Left + Math.Max(0, (SystemParameters.WorkArea.Width - Width) / 2);
            Top = SystemParameters.WorkArea.Top + Math.Max(0, (SystemParameters.WorkArea.Height - Height) / 2);
        }
        
        private void BrowseFolderButton_Click(object sender, RoutedEventArgs e)
        {
            // Use OpenFolderDialog (WPF doesn't have native folder picker, use workaround)
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select folder containing INS files",
                ShowNewFolderButton = false,
                SelectedPath = _currentFolderPath
            };
            
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                string newPath = dialog.SelectedPath;
                _currentFolderPath = newPath;
                SaveSettings();
                LoadInspections(newPath);
            }
        }
        
        private bool _isLoading = false;
        private readonly HashSet<string> _openFilePaths = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _openInspectionTypes = new(StringComparer.OrdinalIgnoreCase);
        
        public async void LoadInspections(string folderPath)
        {
            // Prevent concurrent loads
            if (_isLoading) return;
            _isLoading = true;
            
            _allInspections.Clear();
            _currentFolderPath = folderPath;
            
            // Update folder path display
            FolderPathText.Text = folderPath;
            FolderPathText.ToolTip = folderPath;
            
            if (!Directory.Exists(folderPath))
            {
                StatusText.Text = "Folder not found";
                _isLoading = false;
                return;
            }
            
            // Disable UI during load
            InspectionListView.IsEnabled = false;
            OpenButton.IsEnabled = false;
            StatusText.Text = "Scanning folder...";
            
            try
            {
                var insFiles = await Task.Run(() => 
                    Directory.GetFiles(folderPath, "*.ins", SearchOption.TopDirectoryOnly));
                
                int totalFiles = insFiles.Length;
                
                // Limit to 500 most recent files to prevent long load times on large folders
                const int MaxFiles = 500;
                if (insFiles.Length > MaxFiles)
                {
                    StatusText.Text = $"Loading {MaxFiles} most recent of {totalFiles} files...";
                    
                    // Sort by last modified date and take most recent (do this on background thread)
                    insFiles = await Task.Run(() =>
                        insFiles
                            .Select(f => new { Path = f, Modified = File.GetLastWriteTime(f) })
                            .OrderByDescending(f => f.Modified)
                            .Take(MaxFiles)
                            .Select(f => f.Path)
                            .ToArray());
                }
                else
                {
                    StatusText.Text = $"Loading {insFiles.Length} files...";
                }
                
                CaptureOpenInspectionLocks();

                // Parse files on background thread
                var loadedInspections = await Task.Run(() =>
                {
                    var results = new List<InspectionFileInfo>();
                    foreach (var filePath in insFiles)
                    {
                        try
                        {
                            var info = ParseInspectionFile(filePath);
                            if (info != null)
                                results.Add(info);
                        }
                        catch
                        {
                            // Skip files that can't be parsed
                        }
                    }
                    return results.OrderBy(i => i.Address).ToList();
                });
                
                _allInspections = loadedInspections;
                AssignRowColors();

                if (totalFiles > MaxFiles)
                {
                    StatusText.Text = $"Showing {MaxFiles} most recent of {totalFiles} inspections";
                }
                
                RefreshList();
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Error loading: {ex.Message}";
                System.Diagnostics.Debug.WriteLine($"LoadInspections error: {ex}");
            }
            finally
            {
                InspectionListView.IsEnabled = true;
                _isLoading = false;
            }
        }
        
        private InspectionFileInfo? ParseInspectionFile(string filePath)
        {
            string inspectionCodeFromName = GetInspectionCodeFromFileName(filePath);
            try
            {
                string fullPath = Path.GetFullPath(filePath);
                bool isFileAlreadyOpen = _openFilePaths.Contains(fullPath);

                string json = File.ReadAllText(filePath);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                
                string address = "";
                string inspectionCode = "";
                DateTime date = File.GetLastWriteTime(filePath);
                
                // Try to get Address
                if (root.TryGetProperty("Address", out var addrProp))
                    address = (addrProp.GetString() ?? "").Trim();

                // Try to get Subdivision (stored as Project field)
                string subdivision = "";
                if (root.TryGetProperty("Project", out var projProp))
                    subdivision = (projProp.GetString() ?? "").Trim();

                // Combine address and subdivision
                string fullAddress = address;
                if (!string.IsNullOrWhiteSpace(subdivision))
                    fullAddress = string.IsNullOrWhiteSpace(address) ? subdivision : $"{address}, {subdivision}";
                
                // If no address, use filename
                if (string.IsNullOrWhiteSpace(fullAddress))
                    fullAddress = Path.GetFileNameWithoutExtension(filePath);
                
                // Try to get InspectionCode
                if (root.TryGetProperty("InspectionCode", out var codeProp))
                    inspectionCode = (codeProp.GetString() ?? "").Trim();
                
                // Try to get a date from Events or InspectionDate
                if (root.TryGetProperty("InspectionDate", out var dateProp))
                {
                    if (dateProp.TryGetDateTime(out var inspDate))
                        date = inspDate;
                }
                else if (root.TryGetProperty("Events", out var eventsProp) && eventsProp.ValueKind == JsonValueKind.Array)
                {
                    // Get date from most recent event
                    foreach (var evt in eventsProp.EnumerateArray())
                    {
                        if (evt.TryGetProperty("EventDate", out var evtDateProp))
                        {
                            if (evtDateProp.TryGetDateTime(out var evtDate))
                            {
                                if (evtDate > date)
                                    date = evtDate;
                            }
                        }
                    }
                }
                
                // Prefer company/builder fields; Contact is usually the individual person.
                string builder = FirstStringProperty(root,
                    "BuilderName", "Builder", "ClientName", "Client",
                    "CompanyName", "Company", "CustomerName", "Customer",
                    "OwnerName", "Owner", "Contact");
                
                // Get contact info for notifications
                string contactName = "";
                string contactPhone = "";
                string inspectorName = "";
                string inspectorPhone = "";
                
                if (root.TryGetProperty("Contact", out var contactNameProp))
                    contactName = contactNameProp.GetString() ?? "";
                if (root.TryGetProperty("ContactNumber", out var contactNumProp))
                    contactPhone = contactNumProp.GetString() ?? "";
                if (root.TryGetProperty("InspectorName", out var inspNameProp))
                    inspectorName = inspNameProp.GetString() ?? "";
                if (root.TryGetProperty("InspectorPhone", out var inspPhoneProp))
                    inspectorPhone = inspPhoneProp.GetString() ?? "";
                
                // Get lot number
                string lotNumber = "";
                string[] lotFields = { "Lot", "LotNumber", "LotNum", "LotNo" };
                foreach (var fieldName in lotFields)
                {
                    if (root.TryGetProperty(fieldName, out var lotProp))
                    {
                        lotNumber = lotProp.GetString() ?? "";
                        if (!string.IsNullOrWhiteSpace(lotNumber))
                            break;
                    }
                }
                
                // ── Edit status ──────────────────────────────────────────
                // S = signed, Y = has edits but unsigned, N = untouched
                bool isSigned = false;
                if (root.TryGetProperty("Signature", out var sigProp))
                {
                    var sigStr = sigProp.ValueKind == JsonValueKind.String ? sigProp.GetString() : null;
                    isSigned = !string.IsNullOrWhiteSpace(sigStr) &&
                               !sigStr.Equals("None", StringComparison.OrdinalIgnoreCase);
                }

                bool hasEdits = false;
                if (!isSigned && root.TryGetProperty("Sections", out var sectProp) &&
                    sectProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var sect in sectProp.EnumerateArray())
                    {
                        if (hasEdits) break;
                        if (!sect.TryGetProperty("Items", out var itemsProp)) continue;
                        foreach (var itm in itemsProp.EnumerateArray())
                        {
                            if (itm.TryGetProperty("Value", out var valProp))
                            {
                                string? v = valProp.ValueKind == JsonValueKind.String ? valProp.GetString() : null;
                                if (!string.IsNullOrWhiteSpace(v) &&
                                    !v.Equals("None", StringComparison.OrdinalIgnoreCase))
                                { hasEdits = true; break; }
                            }
                            if (!hasEdits && itm.TryGetProperty("Comments", out var cmtProp))
                            {
                                string? c = cmtProp.ValueKind == JsonValueKind.String ? cmtProp.GetString() : null;
                                if (!string.IsNullOrWhiteSpace(c)) { hasEdits = true; break; }
                            }
                            if (!hasEdits && itm.TryGetProperty("Pictures", out var picProp) &&
                                picProp.ValueKind == JsonValueKind.Array &&
                                picProp.GetArrayLength() > 0)
                            { hasEdits = true; break; }
                        }
                    }
                }

                string editStatus = isSigned ? "S" : (hasEdits ? "Y" : "N");

                return new InspectionFileInfo
                {
                    FilePath = filePath,
                    Address = fullAddress,
                    Subdivision = subdivision,
                    InspectionCode = inspectionCode,
                    Builder = builder,
                    Date = date,
                    ContactName = contactName,
                    ContactPhone = contactPhone,
                    InspectorName = inspectorName,
                    InspectorPhone = inspectorPhone,
                    LotNumber = lotNumber,
                    EditStatus = editStatus,
                    IsFileAlreadyOpen = isFileAlreadyOpen,
                    IsTypeAlreadyOpen = !string.IsNullOrWhiteSpace(inspectionCodeFromName) &&
                                        _openInspectionTypes.Contains(inspectionCodeFromName)
                };
            }
            catch
            {
                // Return basic info from file system
                return new InspectionFileInfo
                {
                    FilePath = filePath,
                    Address = Path.GetFileNameWithoutExtension(filePath),
                    InspectionCode = "",
                    Builder = "",
                    Date = File.GetLastWriteTime(filePath),
                    IsFileAlreadyOpen = _openFilePaths.Contains(Path.GetFullPath(filePath)),
                    IsTypeAlreadyOpen = !string.IsNullOrWhiteSpace(inspectionCodeFromName) &&
                                        _openInspectionTypes.Contains(inspectionCodeFromName)
                };
            }
        }
        
        // Very light pastel backgrounds — white included, easy on the eyes
        private static string FirstStringProperty(JsonElement root, params string[] propertyNames)
        {
            foreach (string propertyName in propertyNames)
            {
                if (root.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
                {
                    string value = prop.GetString()?.Trim() ?? "";
                    if (!string.IsNullOrWhiteSpace(value))
                        return value;
                }
            }

            return "";
        }

        private static readonly string[] PastelPalette = {
            "#FFFFFF", "#FFF8F0", "#F0F8FF", "#F5F0FF", "#F0FFF4",
            "#FFF0F5", "#F0FFFF", "#FFFEF0", "#F0F4FF", "#FFF4F0",
            "#F4FFF0", "#FFF0FF", "#F0FFF8", "#FFFAF0", "#F8F0FF",
        };

        // Dark foregrounds — matched by display name so ME/MP both = "BMEP Rough" = same color
        private static readonly string[] DarkPalette = {
            "#000000", "#8B0000", "#000080", "#1B5E20", "#4A148C",
            "#BF360C", "#006064", "#880E4F", "#0D47A1", "#3E2723",
            "#1A237E", "#33691E", "#4E342E", "#B71C1C", "#01579B",
        };

        // Non-white pastels for date grouping (today stays white)
        private static readonly string[] DatePastelPalette = {
            "#FFF8F0", "#F0F8FF", "#F5F0FF", "#F0FFF4",
            "#FFF0F5", "#F0FFFF", "#FFFEF0", "#F0F4FF",
            "#FFF4F0", "#F4FFF0", "#FFF0FF", "#F0FFF8",
        };

        // Normalize punctuation and whitespace for robust key matching
        // (handles trailing spaces, comma spacing, and case differences in address fields)
        private static string NormalizeKey(string s) =>
            System.Text.RegularExpressions.Regex.Replace(
                System.Text.RegularExpressions.Regex.Replace(s.Trim(), @"[,.\-]", " "),
                @"\s+", " ").ToLowerInvariant();

        private void AssignRowColors()
        {
            var addressBrush = new Dictionary<string, Brush>(StringComparer.OrdinalIgnoreCase);
            var typeBrush    = new Dictionary<string, Brush>(StringComparer.OrdinalIgnoreCase);
            var dateBrush    = new Dictionary<string, Brush>(StringComparer.OrdinalIgnoreCase);
            var builderBrush = new Dictionary<string, Brush>(StringComparer.OrdinalIgnoreCase);
            int pIdx = 0, dIdx = 0, dateIdx = 0, bIdx = 0;
            string todayDisplay = DateTime.Today.ToString("MMM d");

            foreach (var item in _allInspections)
            {
                // Address → pastel background
                string addr = NormalizeKey(item.Address ?? "");
                if (!string.IsNullOrEmpty(addr) && !addressBrush.ContainsKey(addr))
                {
                    var c = (Color)ColorConverter.ConvertFromString(PastelPalette[pIdx % PastelPalette.Length]);
                    addressBrush[addr] = new SolidColorBrush(c);
                    pIdx++;
                }

                // Inspection type → dark foreground (aliased codes share display name)
                string typeName = NormalizeKey(item.InspectionTypeName ?? "");
                if (!string.IsNullOrEmpty(typeName) && !typeBrush.ContainsKey(typeName))
                {
                    var c = (Color)ColorConverter.ConvertFromString(DarkPalette[dIdx % DarkPalette.Length]);
                    typeBrush[typeName] = new SolidColorBrush(c);
                    dIdx++;
                }

                // Date → pastel background; today = white
                string dateKey = item.DateDisplay;
                if (!dateBrush.ContainsKey(dateKey))
                {
                    if (dateKey == todayDisplay)
                        dateBrush[dateKey] = Brushes.White;
                    else
                    {
                        var c = (Color)ColorConverter.ConvertFromString(DatePastelPalette[dateIdx % DatePastelPalette.Length]);
                        dateBrush[dateKey] = new SolidColorBrush(c);
                        dateIdx++;
                    }
                }

                // Builder → dark foreground text
                string builderKey = NormalizeKey(item.Builder ?? "");
                if (!string.IsNullOrEmpty(builderKey) && !builderBrush.ContainsKey(builderKey))
                {
                    var c = (Color)ColorConverter.ConvertFromString(DarkPalette[bIdx % DarkPalette.Length]);
                    builderBrush[builderKey] = new SolidColorBrush(c);
                    bIdx++;
                }

                item.RowBackground     = addressBrush.TryGetValue(addr,      out var bg) ? bg : Brushes.White;
                item.RowForeground     = typeBrush.TryGetValue(typeName,      out var fg) ? fg : Brushes.Black;
                item.DateBackground    = dateBrush.TryGetValue(dateKey,       out var db) ? db : Brushes.White;
                item.BuilderForeground = builderBrush.TryGetValue(builderKey, out var bf) ? bf : Brushes.Black;
            }
        }

        private void RefreshList(string? filter = null)
        {
            foreach (var item in _allInspections)
                item.CategoryName = GetCategoryName(item);

            IEnumerable<InspectionFileInfo> filtered = _allInspections;
            
            if (!string.IsNullOrWhiteSpace(filter))
            {
                string filterLower = filter.ToLower();
                filtered = _allInspections.Where(i =>
                    i.Address.ToLower().Contains(filterLower) ||
                    i.Subdivision.ToLower().Contains(filterLower) ||
                    i.InspectionCode.ToLower().Contains(filterLower) ||
                    i.InspectionTypeName.ToLower().Contains(filterLower) ||
                    i.Builder.ToLower().Contains(filterLower) ||
                    i.FileName.ToLower().Contains(filterLower) ||
                    i.DateDisplay.ToLower().Contains(filterLower));
            }

            var filteredList = filtered.ToList();
            InspectionListView.ItemsSource = filteredList;
            
            int total = _allInspections.Count;
            int shown = filteredList.Count;
            
            if (total == 0)
                StatusText.Text = "No inspections found";
            else if (shown == total)
                StatusText.Text = $"{total} inspection{(total == 1 ? "" : "s")}";
            else
                StatusText.Text = $"Showing {shown} of {total}";
            
            InspectionListView.SelectedItem = null;
            OpenButton.IsEnabled = false;

            // Re-apply persisted sort after ItemsSource is replaced
            ApplyCurrentSort();
            ApplySortIndicators();
        }

        private void ApplyCurrentSort()
        {
            if (InspectionListView.ItemsSource == null) return;
            var view = CollectionViewSource.GetDefaultView(InspectionListView.ItemsSource);
            if (view == null) return;
            view.GroupDescriptions.Clear();
            view.SortDescriptions.Clear();

            if (_categoryMode != "None")
            {
                view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(InspectionFileInfo.CategoryName)));
                view.SortDescriptions.Add(new SortDescription(nameof(InspectionFileInfo.CategoryName), ListSortDirection.Ascending));
            }

            if (!string.IsNullOrEmpty(_sortColumnPath))
                view.SortDescriptions.Add(new SortDescription(_sortColumnPath, _lastDirection));
            view.Refresh();
        }

        private string GetCategoryName(InspectionFileInfo item)
        {
            string value = _categoryMode switch
            {
                "Builder" => item.Builder,
                "Subdivision" => item.Subdivision,
                "JobType" => item.InspectionTypeName,
                _ => ""
            };

            return string.IsNullOrWhiteSpace(value) ? "(Uncategorized)" : value.Trim();
        }

        private void ApplySortIndicators()
        {
            if (!(InspectionListView.View is GridView gridView)) return;
            string arrow = _lastDirection == ListSortDirection.Ascending ? " ▲" : " ▼";
            foreach (var col in gridView.Columns)
            {
                string hdr = col.Header?.ToString()?.Replace(" ▲", "").Replace(" ▼", "") ?? "";
                // Use the same header→path map as the click handler (most columns use CellTemplate, not DisplayMemberBinding)
                if (!_headerSortPaths.TryGetValue(hdr, out string? colPath))
                    colPath = (col.DisplayMemberBinding as Binding)?.Path.Path ?? "";
                col.Header = (!string.IsNullOrEmpty(colPath) && colPath == _sortColumnPath)
                    ? hdr + arrow : hdr;
            }
        }

        private double[] GetColumnWidths()
        {
            if (InspectionListView.View is GridView gridView)
                return gridView.Columns.Select(c => c.Width).ToArray();
            return Array.Empty<double>();
        }

        private void ApplyColumnWidths(double[] widths)
        {
            if (!(InspectionListView.View is GridView gridView)) return;
            for (int i = 0; i < Math.Min(widths.Length, _columnOrder.Count); i++)
            {
                if (widths[i] > 0 && _columnsByKey.TryGetValue(_columnOrder[i], out var column))
                    column.Width = widths[i];
            }
        }

        private void InitializeColumnLayout()
        {
            _columnsByKey.Clear();
            _columnsByKey["Address"] = AddressColumn;
            _columnsByKey["Type"] = TypeColumn;
            _columnsByKey["Date"] = DateColumn;
            _columnsByKey["Builder"] = BuilderColumn;
            _columnsByKey["Filename"] = FilenameColumn;
            _columnsByKey["Open"] = OpenColumn;
            _columnsByKey["Signed"] = SignedColumn;
            _columnsByKey["Call"] = CallColumn;

            _columnOrder.Clear();
            _columnOrder.AddRange(new[] { "Address", "Type", "Date", "Builder", "Filename", "Open", "Signed", "Call" });

            _columnCheckBoxesByKey.Clear();
            _columnCheckBoxesByKey["Address"] = AddressColumnCheckBox;
            _columnCheckBoxesByKey["Type"] = TypeColumnCheckBox;
            _columnCheckBoxesByKey["Date"] = DateColumnCheckBox;
            _columnCheckBoxesByKey["Builder"] = BuilderColumnCheckBox;
            _columnCheckBoxesByKey["Filename"] = FilenameColumnCheckBox;
            _columnCheckBoxesByKey["Open"] = OpenColumnCheckBox;
            _columnCheckBoxesByKey["Signed"] = SignedColumnCheckBox;
            _columnCheckBoxesByKey["Call"] = CallColumnCheckBox;

            _defaultColumnWidthsByKey.Clear();
            foreach (var kvp in _columnsByKey)
                _defaultColumnWidthsByKey[kvp.Key] = kvp.Value.Width;
        }

        private void WatchColumnWidthChanges()
        {
            var descriptor = DependencyPropertyDescriptor.FromProperty(
                GridViewColumn.WidthProperty,
                typeof(GridViewColumn));

            if (descriptor == null)
                return;

            foreach (var column in _columnsByKey.Values)
            {
                descriptor.RemoveValueChanged(column, ColumnWidth_Changed);
                descriptor.AddValueChanged(column, ColumnWidth_Changed);
            }
        }

        private void ColumnWidth_Changed(object? sender, EventArgs e)
        {
            if (_isApplyingColumnSettings || _settings == null)
                return;

            CaptureVisibleColumnWidths();
            _columnWidthSaveTimer.Stop();
            _columnWidthSaveTimer.Start();
        }

        private Dictionary<string, double> GetColumnWidthsByKey()
        {
            var widths = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in _columnsByKey)
            {
                double width = kvp.Value.Width;
                if (!IsColumnInView(kvp.Key) && _settings?.ColumnWidthsByKey != null &&
                    _settings.ColumnWidthsByKey.TryGetValue(kvp.Key, out double savedWidth))
                {
                    width = savedWidth;
                }

                widths[kvp.Key] = width > 4
                    ? width
                    : _defaultColumnWidthsByKey.GetValueOrDefault(kvp.Key, 90);
            }

            return widths;
        }

        private Dictionary<string, bool> GetColumnVisibilityByKey()
        {
            var visibility = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (string key in _columnOrder)
                visibility[key] = GetUserColumnVisibility(key);
            return visibility;
        }

        private void ApplyColumnSettings()
        {
            _isApplyingColumnSettings = true;
            try
            {
                if (InspectionListView.View is not GridView gridView)
                    return;

                if (_hasAppliedColumnSettingsOnce)
                    CaptureVisibleColumnWidths();
                gridView.Columns.Clear();

                foreach (string key in _columnOrder)
                {
                    if (!_columnsByKey.TryGetValue(key, out var column))
                        continue;

                    bool userVisible = GetUserColumnVisibility(key);
                    bool effectiveVisible = userVisible && !ShouldSuppressColumnForCategory(key);

                    if (effectiveVisible)
                    {
                        column.Width = GetSavedColumnWidth(key, column);
                        gridView.Columns.Add(column);
                    }

                    if (_columnCheckBoxesByKey.TryGetValue(key, out var checkBox))
                    {
                        bool suppressed = ShouldSuppressColumnForCategory(key);
                        checkBox.IsChecked = userVisible;
                        checkBox.IsEnabled = !suppressed;
                        checkBox.ToolTip = suppressed
                            ? "Hidden while this category view is active"
                            : null;
                    }
                }
            }
            finally
            {
                _isApplyingColumnSettings = false;
                _hasAppliedColumnSettingsOnce = true;
            }
        }

        private void ColumnVisibilityCheckBox_Changed(object sender, RoutedEventArgs e)
        {
            if (_isApplyingColumnSettings || sender is not CheckBox checkBox)
                return;

            string? key = _columnCheckBoxesByKey.FirstOrDefault(kvp => kvp.Value == checkBox).Key;
            if (string.IsNullOrWhiteSpace(key) || !_columnsByKey.TryGetValue(key, out var column))
                return;

            _settings ??= new PickerSettings();
            if (IsColumnInView(key) && column.Width > 4)
                _settings.ColumnWidthsByKey[key] = column.Width;
            _settings.ColumnVisibility[key] = checkBox.IsChecked == true;

            ApplyColumnSettings();
            SaveSettings();
        }

        private void CaptureVisibleColumnWidths()
        {
            _settings ??= new PickerSettings();
            foreach (var kvp in _columnsByKey)
            {
                if (IsColumnInView(kvp.Key) && kvp.Value.Width > 4)
                    _settings.ColumnWidthsByKey[kvp.Key] = kvp.Value.Width;
            }
        }

        private bool GetUserColumnVisibility(string key)
        {
            if (_settings?.LayoutVersion < 4 && (key == "Open" || key == "Signed"))
                return false;

            if (_settings?.ColumnVisibility != null &&
                _settings.ColumnVisibility.TryGetValue(key, out bool savedVisibility))
            {
                return savedVisibility;
            }

            return key != "Open" && key != "Signed";
        }

        private double GetSavedColumnWidth(string key, GridViewColumn column)
        {
            double width = _defaultColumnWidthsByKey.GetValueOrDefault(key, column.Width);
            if (_settings?.ColumnWidthsByKey != null &&
                _settings.ColumnWidthsByKey.TryGetValue(key, out double savedWidth) &&
                savedWidth > 4)
            {
                width = savedWidth;
            }

            return Math.Max(width, GetMinimumColumnWidth(key));
        }

        private bool IsColumnInView(string key)
        {
            return InspectionListView.View is GridView gridView &&
                   _columnsByKey.TryGetValue(key, out var column) &&
                   gridView.Columns.Contains(column);
        }

        private static double GetMinimumColumnWidth(string key) => key switch
        {
            "Call" => 86,
            "Open" => 64,
            "Signed" => 70,
            "Date" => 82,
            "Filename" => 78,
            _ => 40
        };
        
        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            string filter = SearchBox.Text;
            ClearSearchButton.Visibility = string.IsNullOrEmpty(filter) ? Visibility.Collapsed : Visibility.Visible;
            RefreshList(filter);
        }
        
        private void ClearSearchButton_Click(object sender, RoutedEventArgs e)
        {
            SearchBox.Text = "";
            SearchBox.Focus();
        }
        
        private void GridViewColumnHeader_Click(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is GridViewColumnHeader headerClicked)
            {
                if (headerClicked.Role == GridViewColumnHeaderRole.Padding)
                    return;

                // Strip sort arrows — ApplySortIndicators() appended them to col.Header,
                // so the header text is now e.g. "Date ▲" instead of "Date".
                string? headerText = (headerClicked.Column?.Header as string)
                    ?.Replace(" ▲", "").Replace(" ▼", "");
                if (headerText == null || !_headerSortPaths.TryGetValue(headerText, out string? columnBinding))
                    columnBinding = (headerClicked.Column?.DisplayMemberBinding as Binding)?.Path.Path;
                if (string.IsNullOrEmpty(columnBinding))
                    return;

                // Toggle direction when clicking the already-sorted column; reset to Ascending otherwise
                ListSortDirection direction = (columnBinding == _sortColumnPath)
                    ? (_lastDirection == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending)
                    : ListSortDirection.Ascending;

                var view = CollectionViewSource.GetDefaultView(InspectionListView.ItemsSource);
                view.SortDescriptions.Clear();
                view.SortDescriptions.Add(new SortDescription(columnBinding, direction));
                view.Refresh();

                _lastHeaderClicked = headerClicked;
                _lastDirection     = direction;
                _sortColumnPath    = columnBinding;

                ApplySortIndicators();
            }
        }
        
        private void InspectionListView_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            OpenSelectedInspection();
        }

        private void InspectionListViewItem_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_stayOpenHome || _isLoading || _listScrollStarted)
                return;

            if (e.OriginalSource is DependencyObject source &&
                (FindAncestor<Button>(source) != null || FindAncestor<Expander>(source) != null))
            {
                return;
            }

            if (sender is ListViewItem { DataContext: InspectionFileInfo info })
            {
                ++_selectionOpenRequestId;
                OpenInspection(info);
                InspectionListView.SelectedItem = null;
                _pendingTapInspection = null;
                e.Handled = true;
            }
        }

        private async void InspectionListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_stayOpenHome || _isLoading)
                return;

            if (InspectionListView.SelectedItem is not InspectionFileInfo selected)
                return;

            int requestId = ++_selectionOpenRequestId;
            await Task.Delay(140);

            if (requestId != _selectionOpenRequestId ||
                _listScrollStarted ||
                Mouse.LeftButton == MouseButtonState.Pressed)
            {
                return;
            }

            OpenInspection(selected);
            InspectionListView.SelectedItem = null;
        }
        
        private void InspectionListView_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                OpenSelectedInspection();
                e.Handled = true;
            }
        }
        
        private void OpenButton_Click(object sender, RoutedEventArgs e)
        {
            OpenSelectedInspection();
        }
        
        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            if (_stayOpenHome)
            {
                Close();
                return;
            }

            DialogResult = false;
            Close();
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            var settingsWindow = new SettingsWindow { Owner = this };
            settingsWindow.ShowDialog();
        }
        
        // Keyboard handler - Esc closes, Enter opens selected
        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                if (_stayOpenHome)
                {
                    SearchBox.Text = "";
                    SearchBox.Focus();
                    e.Handled = true;
                    return;
                }

                DialogResult = false;
                Close();
                e.Handled = true;
            }
            else if (e.Key == Key.Enter && !(e.OriginalSource is TextBox))
            {
                OpenSelectedInspection();
                e.Handled = true;
            }
        }
        
        private void OpenSelectedInspection()
        {
            // Don't open while loading
            if (_isLoading) return;
            
            if (InspectionListView.SelectedItem is InspectionFileInfo selected)
            {
                OpenInspection(selected);
            }
        }

        private async void OpenInspection(InspectionFileInfo selected)
        {
            if (_isLoading || selected == null) return;

            if (selected.IsTypeAlreadyOpen)
            {
                StatusText.Text = string.IsNullOrWhiteSpace(selected.InspectionCode)
                    ? "That inspection type is already open."
                    : $"{selected.InspectionTypeName} is already open.";
                return;
            }

            string fullPath = Path.GetFullPath(selected.FilePath);
            if (!_openingFilePaths.Add(fullPath))
            {
                StatusText.Text = $"Already opening {selected.FileName}...";
                return;
            }

            string inspectionCode = GetInspectionCodeFromFileName(selected.FilePath);
            if (_openFilePaths.Contains(fullPath) ||
                (!string.IsNullOrWhiteSpace(inspectionCode) && _openInspectionTypes.Contains(inspectionCode)))
            {
                _openingFilePaths.Remove(fullPath);
                MessageBox.Show(
                    string.IsNullOrWhiteSpace(inspectionCode)
                        ? "That inspection is already open in another RED window."
                        : $"A {inspectionCode} inspection is already open in another RED window.",
                    "Already Open",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                LoadInspections(_currentFolderPath);
                return;
            }

            SelectedFilePath = selected.FilePath;
            StatusText.Text = $"Opening {selected.FileName}...";
            Cursor = Cursors.Wait;
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Render);

            if (_stayOpenHome)
            {
                InspectionOpenRequested?.Invoke(this, selected.FilePath);
                Cursor = Cursors.Arrow;
                _ = ReleaseOpeningGuardAfterRefreshAsync(fullPath);
                return;
            }

            DialogResult = true;
            Close();
        }

        private async Task ReleaseOpeningGuardAfterRefreshAsync(string fullPath)
        {
            await Task.Delay(2500);
            try
            {
                LoadInspections(_currentFolderPath);
            }
            finally
            {
                _openingFilePaths.Remove(fullPath);
            }
        }

        private void CaptureOpenInspectionLocks()
        {
            _openFilePaths.Clear();
            _openInspectionTypes.Clear();

            foreach (var filePath in Directory.GetFiles(Path.GetTempPath(), "red-open-*.lock", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    if (!IsLiveLockMetadata(filePath))
                    {
                        TryDeleteStaleLockMetadata(filePath);
                        continue;
                    }

                    var lines = File.ReadAllLines(filePath);
                    foreach (var rawLine in lines)
                    {
                        var line = rawLine?.Trim();
                        if (string.IsNullOrWhiteSpace(line) || !line.Contains('=')) continue;
                        var parts = line.Split('=', 2);
                        if (parts.Length != 2) continue;

                        if (parts[0].Equals("path", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(parts[1]))
                            _openFilePaths.Add(Path.GetFullPath(parts[1].Trim()));
                        else if (parts[0].Equals("type", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(parts[1]))
                            _openInspectionTypes.Add(parts[1].Trim().ToUpperInvariant());
                    }
                }
                catch
                {
                    // Ignore stale/partial lock metadata files
                }
            }
        }

        private static bool IsLiveLockMetadata(string lockPath)
        {
            string fileName = Path.GetFileNameWithoutExtension(lockPath);
            var parts = fileName.Split('-');
            if (parts.Length < 3 || !int.TryParse(parts[2], out int processId))
                return false;

            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(processId);
                return !process.HasExited;
            }
            catch
            {
                return false;
            }
        }

        private static void TryDeleteStaleLockMetadata(string lockPath)
        {
            try { File.Delete(lockPath); } catch { }
        }

        private static string GetInspectionCodeFromFileName(string filePath)
        {
            var parts = Path.GetFileNameWithoutExtension(filePath).Split('-');
            return parts.Length > 1 ? parts[1].Trim().ToUpperInvariant() : string.Empty;
        }
        
        private void NotifyButton_Click(object sender, RoutedEventArgs e)
        {
            e.Handled = true;

            if (sender is Button btn && btn.Tag is InspectionFileInfo info)
            {
                if (string.IsNullOrWhiteSpace(info.ContactPhone))
                {
                    MessageBox.Show("No contact phone number available for this inspection.", 
                        "Cannot Notify", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                
                var qrWindow = new NotifyQRWindow(info);
                qrWindow.Owner = this;
                qrWindow.ShowDialog();
            }
        }
        
        #region Mouse-based Scrolling (tablet reports touch as mouse)

        private void InspectionListView_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not ListView listView)
                return;

            if (e.OriginalSource is DependencyObject headerSource &&
                IsColumnHeaderInteraction(headerSource))
            {
                _listIsScrolling = false;
                _listScrollStarted = false;
                _listScrollStartPoint = null;
                _pendingTapInspection = null;
                return;
            }

            var scrollViewer = GetScrollViewer(listView);
            _listScrollStartPoint  = e.GetPosition(listView);
            _listScrollStartVerticalOffset = scrollViewer?.VerticalOffset ?? 0;
            _listScrollStartHorizontalOffset = scrollViewer?.HorizontalOffset ?? 0;
            _listIsScrolling  = true;
            _listScrollStarted = false;
            _pendingTapInspection = null;

            if (listView == InspectionListView &&
                _stayOpenHome &&
                e.OriginalSource is DependencyObject source &&
                FindAncestor<Expander>(source) == null &&
                FindAncestor<Button>(source) == null &&
                GetInspectionInfoFromSource(listView, source) is InspectionFileInfo info)
            {
                _pendingTapInspection = info;
            }
        }

        private void InspectionListView_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (sender is not ListView listView)
                return;

            if (e.OriginalSource is DependencyObject headerSource &&
                IsColumnHeaderInteraction(headerSource))
            {
                return;
            }

            if (!_listIsScrolling || !_listScrollStartPoint.HasValue) return;
            if (e.LeftButton != MouseButtonState.Pressed) return;

            Point currentPoint = e.GetPosition(listView);
            double deltaX = _listScrollStartPoint.Value.X - currentPoint.X;
            double deltaY = _listScrollStartPoint.Value.Y - currentPoint.Y;

            if (!_listScrollStarted && (Math.Abs(deltaY) > 15 || Math.Abs(deltaX) > 15))
            {
                _listScrollStarted = true;
                Mouse.Capture(listView); // Capture so drag stays tracked outside items
            }

            if (_listScrollStarted)
            {
                var scrollViewer = GetScrollViewer(listView);
                if (scrollViewer != null)
                {
                    scrollViewer.ScrollToVerticalOffset(_listScrollStartVerticalOffset + deltaY);
                    scrollViewer.ScrollToHorizontalOffset(_listScrollStartHorizontalOffset + deltaX);
                }
                e.Handled = true;
            }
        }

        private void InspectionListView_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is not ListView listView)
                return;

            if (e.OriginalSource is DependencyObject headerSource &&
                IsColumnHeaderInteraction(headerSource))
            {
                _listIsScrolling = false;
                _listScrollStartPoint = null;
                _listScrollStarted = false;
                _pendingTapInspection = null;
                _columnWidthSaveTimer.Stop();
                CaptureVisibleColumnWidths();
                SaveSettings();
                return;
            }

            bool wasScrolling = _listScrollStarted;
            _listIsScrolling   = false;
            _listScrollStartPoint = null;
            _listScrollStarted = false;
            var pendingTapInspection = _pendingTapInspection;
            _pendingTapInspection = null;

            if (wasScrolling)
            {
                Mouse.Capture(null);
                e.Handled = true; // Block click-to-select only when we actually scrolled
            }
            else if (listView == InspectionListView && _stayOpenHome)
            {
                if (pendingTapInspection != null)
                {
                    OpenInspection(pendingTapInspection);
                    InspectionListView.SelectedItem = null;
                    e.Handled = true;
                }
            }
        }

        private static T? FindAncestor<T>(DependencyObject current) where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T target)
                    return target;
                current = VisualTreeHelper.GetParent(current);
            }
            return null;
        }

        private static bool IsColumnHeaderInteraction(DependencyObject source)
        {
            return FindAncestor<GridViewColumnHeader>(source) != null ||
                   FindAncestor<Thumb>(source) != null;
        }

        private static InspectionFileInfo? GetInspectionInfoFromSource(ListView listView, DependencyObject source)
        {
            if (ItemsControl.ContainerFromElement(listView, source) is ListViewItem item &&
                item.DataContext is InspectionFileInfo info)
            {
                return info;
            }

            return FindAncestor<ListViewItem>(source)?.DataContext as InspectionFileInfo;
        }

        private ScrollViewer? GetScrollViewer(DependencyObject depObj)
        {
            if (depObj is ScrollViewer sv) return sv;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(depObj); i++)
            {
                var result = GetScrollViewer(VisualTreeHelper.GetChild(depObj, i));
                if (result != null) return result;
            }
            return null;
        }
        
        #endregion
    }
    
    public class InspectionFileInfo
    {
        public string FilePath { get; set; } = "";
        public string Address { get; set; } = "";
        public string Subdivision { get; set; } = "";
        public string InspectionCode { get; set; } = "";
        public string Builder { get; set; } = "";
        public DateTime Date { get; set; }
        
        // Contact info for notifications
        public string ContactName { get; set; } = "";
        public string ContactPhone { get; set; } = "";
        public string InspectorName { get; set; } = "";
        public string InspectorPhone { get; set; } = "";
        public string LotNumber { get; set; } = "";
        
        public string EditStatus { get; set; } = "N";
        public string CategoryName { get; set; } = "";

        // Set by AssignRowColors() after load
        public Brush RowBackground    { get; set; } = Brushes.Transparent; // pastel bg per address
        public Brush RowForeground    { get; set; } = Brushes.Black;       // dark fg per inspection type
        public Brush DateBackground   { get; set; } = Brushes.White;       // pastel bg per date (today = white)
        public Brush BuilderForeground { get; set; } = Brushes.Black;      // dark fg per builder

        public string DateDisplay => Date.ToString("MMM d");
        public string FileName => Path.GetFileNameWithoutExtension(FilePath);
        public int AttemptNumberSort => TryGetAttemptNumber(FileName, out int attempt) ? attempt : int.MaxValue;
        public string AttemptNumberDisplay => TryGetAttemptNumber(FileName, out int attempt) ? attempt.ToString() : "";
        public bool HasContactPhone => !string.IsNullOrWhiteSpace(ContactPhone);
        public bool IsFileAlreadyOpen { get; set; }
        public bool IsTypeAlreadyOpen { get; set; }
        public string OpenStatus => IsFileAlreadyOpen ? "Open" : (IsTypeAlreadyOpen ? "Type Open" : "");
        public bool CanOpenInspection => !IsTypeAlreadyOpen;
        public bool CanNotify => HasContactPhone && !IsTypeAlreadyOpen;

        public string InspectionTypeName => _typeNames.TryGetValue(InspectionCode ?? "", out var name) ? name : InspectionCode;

        private static readonly Dictionary<string, string> _typeNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["AFI"]  = "ACCA 310 Field Inspection",
            ["BC"]   = "Builder Confirmation",
            ["BF"]   = "BMEP Final",
            ["BWT"]  = "New Home Orientation",
            ["COH"]  = "Flashing Sheathing Framing (COH)",
            ["CPP"]  = "Concrete Pre-pour",
            ["CPR"]  = "Concrete Pour",
            ["FS"]   = "Frame",
            ["FSF"]  = "Flashing Sheathing Framing",
            ["FWI"]  = "Stage Three Fire",
            ["HEF"]  = "Final Energy Testing",
            ["HER"]  = "HERS Energy Rough",
            ["HET"]  = "Energy Star Final Testing",
            ["IAP"]  = "Indoor Air Plus",
            ["IEF"]  = "Energy Star Final",
            ["IER"]  = "Energy Star Rough",
            ["ME"]   = "BMEP Rough",
            ["MP"]   = "BMEP Rough",
            ["PLY"]  = "Polyseal",
            ["PPE"]  = "Concrete Post Pour Elevations",
            ["QIER"] = "Energy Rough",
            ["SCI"]  = "Special Consult",
            ["SRP"]  = "Slab Repair Pre Pour",
            ["STR"]  = "Stressing",
            ["SWD"]  = "SWD",
            ["SWI"]  = "Shearwall Inspection",
            ["TFF"]  = "TDI Final Frame",
            ["TPC"]  = "TDI Pre Cornice",
            ["TRDI"] = "TDI Roof Decking Inspection",
            ["TRSI"] = "TDI Roof Shingle Inspection",
        };

        private static bool TryGetAttemptNumber(string fileName, out int attempt)
        {
            attempt = 0;
            if (string.IsNullOrWhiteSpace(fileName))
                return false;

            var matches = System.Text.RegularExpressions.Regex.Matches(fileName, @"-(\d+)(?=-)");
            for (int i = matches.Count - 1; i >= 0; i--)
            {
                if (int.TryParse(matches[i].Groups[1].Value, out attempt))
                    return true;
            }

            return false;
        }
    }

    public class OrderFileInfo
    {
        public string StatusGroup { get; set; } = "Unassigned";
        public string JobNumber { get; set; } = "";
        public string Address { get; set; } = "";
        public string Subdivision { get; set; } = "";
        public string Builder { get; set; } = "";
        public string City { get; set; } = "";
        public string Service { get; set; } = "";
        public DateTime? ScheduleDate { get; set; }
        public string ScheduleDateText => ScheduleDate?.ToString("M/d/yyyy") ?? "";
        public string Assigned { get; set; } = "";
        public string Note { get; set; } = "";
    }

    public class PickerSettings
    {
        public int      LayoutVersion  { get; set; } = 0;
        public string   LastFolderPath { get; set; } = "";
        public double[] ColumnWidths   { get; set; } = Array.Empty<double>();
        public Dictionary<string, double> ColumnWidthsByKey { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, bool>   ColumnVisibility  { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public string   SortColumn     { get; set; } = "";
        public string   SortDirection  { get; set; } = "Ascending";
        public double   WindowLeft     { get; set; } = double.NaN;
        public double   WindowTop      { get; set; } = double.NaN;
        public double   WindowWidth    { get; set; } = 0;
        public double   WindowHeight   { get; set; } = 0;
        public string   WindowState    { get; set; } = "Normal";
        public double   ListFontSize   { get; set; } = 16;
        public bool     SearchBarVisible { get; set; } = true;
        public string   CategoryMode   { get; set; } = "None";
    }
}
