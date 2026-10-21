using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using InspectionEditor.Services;

namespace InspectionEditor
{
    // ORDERS tab: the live Strand service-request schedule, matching Inspect2026's Schedule tab.
    public partial class InspectionPickerWindow
    {
        private static readonly TimeSpan OrdersRefreshInterval = TimeSpan.FromMinutes(5);

        private static readonly Dictionary<string, string> _orderHeaderSortPaths = new()
        {
            { "Job",             nameof(StrandScheduleRequest.JobNumber) },
            { "Address",         nameof(StrandScheduleRequest.Address) },
            { "Subdivision",     nameof(StrandScheduleRequest.SubdivisionName) },
            { "Builder",         nameof(StrandScheduleRequest.BuilderName) },
            { "City",            nameof(StrandScheduleRequest.City) },
            { "Service",         nameof(StrandScheduleRequest.ServiceName) },
            { "Schedule Date",   nameof(StrandScheduleRequest.Date) },
            { "Assigned",        nameof(StrandScheduleRequest.AssignedName) },
            { "Note",            nameof(StrandScheduleRequest.SpecialRequest) },
        };

        private const string OrdersSignInPrompt = "Sign in to Strand to load the live schedule.";

        private DispatcherTimer? _ordersRefreshTimer;
        private readonly HashSet<string> _collapsedOrderGroups = new(StringComparer.OrdinalIgnoreCase);
        private int _ordersLoadVersion = 0;
        private bool _ordersBusy;
        private bool _ordersDialogOpen;
        private bool _ordersLoadedOnce;
        private bool _ordersSignedOutManually;
        private string _ordersSortPath = nameof(StrandScheduleRequest.Date);
        private ListSortDirection _ordersSortDirection = ListSortDirection.Ascending;
        private string _lastStrandUsername = "";
        private ListViewItem? _pressedOrderItem;
        private bool _pressedOrderInSelectColumn;

        private void ShowOrdersTab()
        {
            // Locked until the inspector profile is complete (owner, 2026-10-18).
            if (_profileLocked)
            {
                ShowSettingsTab();
                return;
            }

            MyListPanel.Visibility = Visibility.Collapsed;
            SettingsPanel.Visibility = Visibility.Collapsed;
            OrdersPanel.Visibility = Visibility.Visible;
            BuyerWalkPanel.Visibility = Visibility.Collapsed;
            AboutPanel.Visibility = Visibility.Collapsed;
            SetActiveTab(OrdersTabButton);
            UpdateOrdersSignInUi();

            if (StrandScheduleService.IsSignedIn)
                _ = LoadOrdersAsync();
            else if (!_ordersBusy && !_ordersSignedOutManually)
                _ = SignInAndLoadOrdersAsync(trySavedLogin: true, promptIfNeeded: !_ordersLoadedOnce);
        }

        private async void RefreshOrdersButton_Click(object sender, RoutedEventArgs e)
        {
            if (StrandScheduleService.IsSignedIn)
                await LoadOrdersAsync();
            else
                await SignInAndLoadOrdersAsync(trySavedLogin: true, promptIfNeeded: true);
        }

        private async void OrdersSignInButton_Click(object sender, RoutedEventArgs e)
        {
            if (StrandScheduleService.IsSignedIn)
            {
                EndStrandSession("Signed out of Strand.");
                OfflineCacheService.RefuseSavedLoginForThisRun(); // F-020 R2a: an explicit sign-out; no download on the saved login until the next sign-in
                _ordersSignedOutManually = true;
                return;
            }

            // An explicit Sign In always asks, so a different account can be used.
            await SignInAndLoadOrdersAsync(trySavedLogin: false, promptIfNeeded: true);
        }

        // Signs out and invalidates any sign-in or load still in flight, so it cannot undo this.
        private void EndStrandSession(string status)
        {
            _strandSessionVersion++;
            _ordersLoadVersion++;
            StrandScheduleService.SignOut();
            OfflineCacheService.DeleteJobsCache(); // F-020 C2: the jobs list holds personal data; it goes with the session
            _ordersSessionGeneration = StrandScheduleService.SessionGeneration;
            _ordersRefreshTimer?.Stop();
            OrdersListView.ItemsSource = null;
            OrdersStatusText.Text = status;
            UpdateOrdersSignInUi();
            UpdateOrdersSelectionUi();
            ClearBuyerWalks(); // F-025 §4
        }

        // The Strand session is shared by every picker window. If another window signed in as
        // someone else or signed out, drop this window's rows and catch up when it is activated.
        private int _ordersSessionGeneration;

        private void SyncOrdersWithStrandSession()
        {
            if (_ordersBusy || _buyerWalksBusy || _pickerClosed || _ordersSessionGeneration == StrandScheduleService.SessionGeneration)
                return;

            _ordersSessionGeneration = StrandScheduleService.SessionGeneration;
            _strandSessionVersion++;
            _ordersLoadVersion++;
            _ordersRefreshTimer?.Stop();
            OrdersListView.ItemsSource = null;
            OrdersStatusText.Text = StrandScheduleService.IsSignedIn ? "" : OrdersSignInPrompt;
            UpdateOrdersSignInUi();
            UpdateOrdersSelectionUi();
            ClearBuyerWalks(); // F-025 §4
            if (StrandScheduleService.IsSignedIn && OrdersPanel.Visibility == Visibility.Visible)
                _ = LoadOrdersAsync();
            if (StrandScheduleService.IsSignedIn && BuyerWalkPanel.Visibility == Visibility.Visible)
                _ = LoadBuyerWalksAsync();
        }

        // Signs in with the saved/Inspect login when possible; only asks when that fails.
        private async Task SignInAndLoadOrdersAsync(bool trySavedLogin, bool promptIfNeeded)
        {
            if (_ordersBusy)
                return;

            _ordersLoadedOnce = true;
            bool triedSaved = false;
            if (trySavedLogin)
            {
                var result = await TrySilentStrandSignInAsync();
                if (result.SignedIn)
                {
                    _ordersSignedOutManually = false;
                    await LoadOrdersAsync();
                    return;
                }
                triedSaved = result.Tried;
            }

            if (_pickerClosed)
                return;

            // The silent attempt can take up to 30 s per login. Never pop a modal sign-in over the editor or
            // another tab the user has moved to; keep the "sign-in failed for X" status instead.
            if (trySavedLogin && (OrdersPanel.Visibility != Visibility.Visible || !IsActive))
                promptIfNeeded = false;

            if (!promptIfNeeded)
            {
                // Keep a specific "sign-in failed for X" message; otherwise explain what to do.
                if (!triedSaved && !StrandScheduleService.IsSignedIn)
                    OrdersStatusText.Text = OrdersSignInPrompt;
                return;
            }

            string lastUsername = _lastStrandUsername.Length > 0
                ? _lastStrandUsername
                : StrandCredentialService.GetEffective()?.Username ?? "";
            var signIn = new StrandSignInWindow(lastUsername) { Owner = this };
            _ordersDialogOpen = true;
            bool? ok;
            try { ok = signIn.ShowDialog(); }
            finally { _ordersDialogOpen = false; }
            if (ok != true)
            {
                if (!triedSaved)
                    OrdersStatusText.Text = OrdersSignInPrompt;
                UpdateOrdersSignInUi();
                return;
            }

            var credentials = new StrandCredentials
            {
                Username = signIn.Username,
                Password = signIn.Password,
                ApiUrl = StrandCredentialService.ImportFromInspect()?.ApiUrl ?? "",
                Source = "RED"
            };
            if (!await TryStrandSignInAsync(credentials))
                return;

            _ordersSignedOutManually = false;
            await LoadOrdersAsync();
        }

        // Returns true when the grid was refreshed from Strand.
        private async Task<bool> LoadOrdersAsync()
        {
            if (_ordersBusy || _pickerClosed)
                return false;

            int loadVersion = ++_ordersLoadVersion;
            int generation = StrandScheduleService.SessionGeneration;
            SetOrdersBusy(true, "Loading orders from Strand...");

            try
            {
                var rows = await StrandScheduleService.GetRequestListAsync();
                // Drop rows fetched under a session that another window has since replaced.
                if (loadVersion != _ordersLoadVersion || _pickerClosed || generation != StrandScheduleService.SessionGeneration)
                    return false;

                // Capture after the await so checks and scrolling done during the load are kept.
                var selectedIds = OrdersListView.SelectedItems.OfType<StrandScheduleRequest>().Select(r => r.Id).ToHashSet();
                double verticalOffset = GetScrollViewer(OrdersListView)?.VerticalOffset ?? 0;

                OrdersListView.ItemsSource = rows;
                ApplyOrdersView();
                UpdateOrderFilterUi();
                // Only rows the filter shows: WPF would otherwise park hidden rows and re-select them later.
                foreach (var row in rows.Where(r => selectedIds.Contains(r.Id) && OrdersFilterPredicate(r)))
                    OrdersListView.SelectedItems.Add(row);
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => GetScrollViewer(OrdersListView)?.ScrollToVerticalOffset(verticalOffset));

                OrdersStatusText.Text = $"{rows.Count} requests from the last 7 days through next month. Updated {DateTime.Now:h:mm tt}; refreshes every 5 minutes.";
                _ordersSessionGeneration = generation;
                StartOrdersRefreshTimer();
                return true;
            }
            catch (StrandAuthRequiredException ex)
            {
                if (loadVersion == _ordersLoadVersion)
                {
                    OrdersListView.ItemsSource = null;
                    OrdersStatusText.Text = ex.Message;
                    _ordersRefreshTimer?.Stop();
                    _ordersSessionGeneration = StrandScheduleService.SessionGeneration;
                }
                return false;
            }
            catch (Exception ex)
            {
                if (loadVersion == _ordersLoadVersion)
                {
                    OrdersStatusText.Text = $"Could not load orders from Strand: {ex.Message}";
                    // Retry on the normal 5-minute schedule, even if the first load failed.
                    if (StrandScheduleService.IsSignedIn)
                        StartOrdersRefreshTimer();
                }
                return false;
            }
            finally
            {
                SetOrdersBusy(false);
                UpdateOrdersSignInUi();
                UpdateOrdersSelectionUi();
            }
        }

        private void ApplyOrdersView()
        {
            var view = CollectionViewSource.GetDefaultView(OrdersListView.ItemsSource);
            if (view == null)
                return;

            using (view.DeferRefresh())
            {
                view.Filter = OrdersFilterPredicate;
                view.GroupDescriptions.Clear();
                view.SortDescriptions.Clear();
                view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(StrandScheduleRequest.StatusGroup)));
                view.SortDescriptions.Add(new SortDescription(nameof(StrandScheduleRequest.StatusGroupOrder), ListSortDirection.Ascending));
                view.SortDescriptions.Add(new SortDescription(_ordersSortPath, _ordersSortDirection));
                if (_ordersSortPath != nameof(StrandScheduleRequest.Address))
                    view.SortDescriptions.Add(new SortDescription(nameof(StrandScheduleRequest.Address), ListSortDirection.Ascending));
            }
        }

        // The window's Closed handler stops this timer; a closed picker never restarts it.
        private void StartOrdersRefreshTimer()
        {
            if (_pickerClosed)
                return;

            if (_ordersRefreshTimer == null)
            {
                _ordersRefreshTimer = new DispatcherTimer { Interval = OrdersRefreshInterval };
                _ordersRefreshTimer.Tick += async (_, _) =>
                {
                    if (_pickerClosed)
                    {
                        _ordersRefreshTimer?.Stop();
                        return;
                    }
                    if (_ordersDialogOpen || _ordersBusy || !StrandScheduleService.IsSignedIn)
                        return;
                    await LoadOrdersAsync();
                };
            }

            _ordersRefreshTimer.Stop();
            _ordersRefreshTimer.Start();
        }

        private void SetOrdersBusy(bool busy, string? message = null)
        {
            _ordersBusy = busy;
            if (message != null)
                OrdersStatusText.Text = message;
            RefreshOrdersButton.IsEnabled = !busy;
            OrdersSignInButton.IsEnabled = !busy;
            StrandTestSaveButton.IsEnabled = !busy;
            StrandClearButton.IsEnabled = !busy;
            ProfileImportButton.IsEnabled = !busy; // the import can replace the Strand login
            ProfileBannerImportButton.IsEnabled = !busy;
            OrdersListView.Cursor = busy ? Cursors.Wait : null;
            UpdateOrdersSelectionUi();
        }

        private void UpdateOrdersSignInUi()
        {
            OrdersSignInButton.Content = StrandScheduleService.IsSignedIn ? "Sign Out" : "Sign In";
            OrdersSignedInText.Text = StrandScheduleService.IsSignedIn
                ? $"Signed in as {StrandScheduleService.DisplayName}"
                : "Not signed in";
        }

        private void UpdateOrdersSelectionUi()
        {
            int count = OrdersListView.SelectedItems.Count;
            bool canAct = !_ordersBusy && StrandScheduleService.IsSignedIn;
            OrdersOpenButton.IsEnabled = canAct && count > 0;
            // Like Inspect: Delete only for users with the Strand "Service Request Delete" or "Edit - All" permission.
            OrdersDeleteButton.Visibility = StrandScheduleService.CanDeleteRequests ? Visibility.Visible : Visibility.Collapsed;
            OrdersDeleteButton.IsEnabled = canAct && count > 0 && StrandScheduleService.CanDeleteRequests;
            OrdersAddToMyListButton.IsEnabled = canAct && count > 0;
            OrdersUnassignButton.IsEnabled = canAct && count > 0;
            OrdersSelectionText.Text = count == 0 ? "" : $"{count} selected";
            UpdateOrderGroupCheckBoxes();
            UpdateOrderFilterUi();
        }

        // Group-row checkboxes, like Inspect's: checked when every order in the group is selected.
        private readonly HashSet<CheckBox> _orderGroupCheckBoxes = new();

        private void OrdersGroupCheckBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox box)
            {
                _orderGroupCheckBoxes.Add(box);
                UpdateOrderGroupCheckBox(box);
            }
        }

        private void OrdersGroupCheckBox_Unloaded(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox box)
                _orderGroupCheckBoxes.Remove(box);
        }

        private void OrdersGroupCheckBox_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not CheckBox { DataContext: CollectionViewGroup group } box)
                return;

            var rows = group.Items.OfType<StrandScheduleRequest>().ToList();
            var selected = OrdersListView.SelectedItems.OfType<StrandScheduleRequest>().ToHashSet();
            bool selectAll = rows.Any(r => !selected.Contains(r));
            foreach (var row in rows)
            {
                if (selectAll && !selected.Contains(row))
                    OrdersListView.SelectedItems.Add(row);
                else if (!selectAll && selected.Contains(row))
                    OrdersListView.SelectedItems.Remove(row);
            }
            UpdateOrderGroupCheckBox(box);
        }

        private void UpdateOrderGroupCheckBoxes()
        {
            foreach (var box in _orderGroupCheckBoxes)
                UpdateOrderGroupCheckBox(box);
        }

        private void UpdateOrderGroupCheckBox(CheckBox box)
        {
            if (box.DataContext is not CollectionViewGroup group)
                return;
            var rows = group.Items.OfType<StrandScheduleRequest>().ToList();
            var selected = OrdersListView.SelectedItems.OfType<StrandScheduleRequest>().ToHashSet();
            box.IsChecked = rows.Count > 0 && rows.All(selected.Contains);
        }

        private List<StrandScheduleRequest> GetSelectedOrders() =>
            OrdersListView.SelectedItems.OfType<StrandScheduleRequest>().ToList();

        private void OrdersListView_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateOrdersSelectionUi();

        // The checkbox column (anywhere in that cell) toggles selection; a tap anywhere else opens the request.
        // Column reordering is off for this grid, so the select column is always Columns[0].
        private bool IsInOrderSelectColumn(ListViewItem item, MouseButtonEventArgs e) =>
            OrdersListView.View is GridView { Columns.Count: > 0 } view &&
            e.GetPosition(item).X < view.Columns[0].ActualWidth;

        // Decide on release, for the row that was pressed. A drag-scroll captures the mouse on the
        // ListView, so its release never reaches the row and nothing is toggled or opened.
        private void OrderRow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not ListViewItem item)
                return;

            _pressedOrderItem = item;
            _pressedOrderInSelectColumn = IsInOrderSelectColumn(item, e);
            e.Handled = true; // keeps the CheckBox and ListViewItem from toggling on their own
        }

        private async void OrderRow_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            var pressed = _pressedOrderItem;
            _pressedOrderItem = null;
            if (sender is not ListViewItem { DataContext: StrandScheduleRequest request } item)
                return;

            e.Handled = true;
            if (!ReferenceEquals(item, pressed))
                return;

            if (_pressedOrderInSelectColumn)
                item.IsSelected = !item.IsSelected;
            else
                await OpenOrderAsync(request);
        }

        // Like Inspect, Open shows every selected order's dialog in turn, reloading after each.
        private async void OrdersOpenButton_Click(object sender, RoutedEventArgs e)
        {
            var ids = GetSelectedOrders().Select(r => r.Id).ToList();
            foreach (var id in ids)
            {
                if (_pickerClosed || !StrandScheduleService.IsSignedIn)
                    break;
                var request = (OrdersListView.ItemsSource as IEnumerable<StrandScheduleRequest>)?.FirstOrDefault(r => r.Id == id);
                // Stop opening the rest once a dialog sent the user to Settings.
                if (request != null && !await OpenOrderAsync(request))
                    break;
            }
        }

        // Returns false when the dialog sent the user to Settings (an incomplete inspector profile).
        private async Task<bool> OpenOrderAsync(StrandScheduleRequest request)
        {
            if (_ordersBusy || _pickerClosed)
                return true;
            if (!StrandScheduleService.IsSignedIn)
            {
                await SignInAndLoadOrdersAsync(trySavedLogin: true, promptIfNeeded: true);
                return true;
            }

            var window = new OrderRequestWindow(request.Id) { Owner = this };
            _ordersDialogOpen = true;
            try { window.ShowDialog(); }
            finally { _ordersDialogOpen = false; }

            // Add to My List was refused for an incomplete profile: show Settings now, before the reload, so a slow
            // reload cannot pull the user off a tab they move to meanwhile.
            bool openSettings = window.OpenSettingsRequested && !_pickerClosed;
            if (openSettings)
                ShowSettingsTab();
            else if (window.AddToMyListRequested && !_pickerClosed)
                await AddOrdersToMyListAsync(new List<StrandScheduleRequest> { request }, skipRowChecks: true, window.CheckedProfile, window.AddedItem);

            // Like Inspect, reload whenever the dialog closes (also after Add to My List, even if it stopped early).
            // This also resets the UI if the session expired.
            await LoadOrdersAsync();
            return !openSettings;
        }

        // Unassign (owner 2026-10-20; Assign is gone): INSPECT's Assign with its "[Unassigned]" row. Anyone may unassign
        // their own orders; only the super user may unassign someone else's. Blocked rows are skipped and listed once,
        // after the reload.
        private async void OrdersUnassignButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = GetSelectedOrders();
            if (selected.Count == 0 || _ordersBusy || _pickerClosed || !StrandScheduleService.IsSignedIn)
                return;

            SetOrdersBusy(true, "Unassigning...");
            UnassignPlan? plan = null;
            string? result = null;
            try
            {
                // Judge each order by the server's list as it is now, not the grid (which can be minutes old), so an
                // order reassigned or closed since the last load is not unassigned by mistake.
                int generation = StrandScheduleService.SessionGeneration;
                var current = (await StrandScheduleService.GetRequestListAsync())
                    .GroupBy(r => r.Id).ToDictionary(g => g.Key, g => g.First());
                if (generation != StrandScheduleService.SessionGeneration)
                    throw new InvalidOperationException("The Strand account changed while this request was running. Refresh to try again.");
                plan = OrdersAddRules.PlanUnassign(selected, current, StrandScheduleService.Username, StrandScheduleService.IsSuperUser);
                if (plan.Rows.Count > 0)
                {
                    await StrandScheduleService.UnassignAsync(plan.Rows.Select(r => r.Id));
                    result = OrdersAddRules.UnassignResult(plan.Rows.Count);
                }
            }
            catch (Exception ex)
            {
                plan = null;
                ShowOrdersMessage($"Could not unassign the selected requests.{Environment.NewLine}{Environment.NewLine}{ex.Message}", "Unassign", MessageBoxImage.Warning);
            }
            finally
            {
                SetOrdersBusy(false);
                UpdateOrdersSignInUi();
            }
            if (_pickerClosed)
                return;

            // Unassigned rows are done; don't let them ride along into the next bulk action.
            if (result != null)
                OrdersListView.UnselectAll();
            bool reloaded = await LoadOrdersAsync();
            // Keep the reload's own message (including a failure) after the result.
            if (result != null && StrandScheduleService.IsSignedIn)
                OrdersStatusText.Text = reloaded ? result : $"{result} {OrdersStatusText.Text}";
            if (plan is { Skipped.Count: > 0 })
                ShowOrdersMessage(string.Join(Environment.NewLine + Environment.NewLine, plan.Skipped), "Unassign", MessageBoxImage.Information);
        }

        private async void OrdersDeleteButton_Click(object sender, RoutedEventArgs e)
        {
            var selected = GetSelectedOrders();
            if (selected.Count == 0 || !StrandScheduleService.CanDeleteRequests)
                return;

            var reasonWindow = new OrderDeleteReasonWindow(selected.Count) { Owner = this };
            _ordersDialogOpen = true;
            bool? ok;
            try { ok = reasonWindow.ShowDialog(); }
            finally { _ordersDialogOpen = false; }
            if (ok != true)
                return;

            SetOrdersBusy(true, "Deleting...");
            var failed = new List<string>();
            var done = new List<StrandScheduleRequest>();
            foreach (var request in selected)
            {
                try
                {
                    await StrandScheduleService.DeleteAsync(request.Id, reasonWindow.ReasonText);
                    done.Add(request);
                }
                catch (Exception ex) { failed.Add($"{request.ServiceName} for {request.Address}: {ex.Message}"); }
            }
            SetOrdersBusy(false);
            if (_pickerClosed)
                return;
            foreach (var request in done)
                OrdersListView.SelectedItems.Remove(request);

            int deleted = selected.Count - failed.Count;
            string message = $"Successfully deleted {deleted} out of {selected.Count} inspections.";
            if (failed.Count > 0)
                message += $"{Environment.NewLine}{Environment.NewLine}Unable to delete:{Environment.NewLine}{string.Join(Environment.NewLine, failed)}";
            MessageBox.Show(this, message, "Delete Request", MessageBoxButton.OK, failed.Count > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);

            await LoadOrdersAsync();
        }

        private void OrdersColumnHeader_Click(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is not GridViewColumnHeader { Column: not null } header ||
                header.Column.Header is not string headerText ||
                !_orderHeaderSortPaths.TryGetValue(headerText, out string? path))
            {
                return;
            }

            _ordersSortDirection = path == _ordersSortPath && _ordersSortDirection == ListSortDirection.Ascending
                ? ListSortDirection.Descending
                : ListSortDirection.Ascending;
            _ordersSortPath = path;
            ApplyOrdersView();
        }

        // Group expansion survives the 5-minute refresh, like Inspect's ExpandedGroupRows.
        private void OrdersGroupExpander_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is Expander { DataContext: CollectionViewGroup group } expander)
                expander.IsExpanded = !_collapsedOrderGroups.Contains(group.Name?.ToString() ?? "");
        }

        private void OrdersGroupExpander_Expanded(object sender, RoutedEventArgs e)
        {
            if (sender is Expander { DataContext: CollectionViewGroup group })
                _collapsedOrderGroups.Remove(group.Name?.ToString() ?? "");
        }

        private void OrdersGroupExpander_Collapsed(object sender, RoutedEventArgs e)
        {
            if (sender is Expander { DataContext: CollectionViewGroup group })
                _collapsedOrderGroups.Add(group.Name?.ToString() ?? "");
        }
    }
}
