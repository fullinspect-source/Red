using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using InspectionEditor.Services;

namespace InspectionEditor
{
    // F-025 Buyer Walk tab = INSPECT's "Buyer Walk" view: the Strand website's buyer walks, grouped by GroupingField,
    // with the ORDERS filter row, INSPECT's row-tap rule, Open (Edit Schedule) and Add to My List. No timer (L7).
    public partial class InspectionPickerWindow
    {
        public const string BuyerWalkSignInPrompt = "Sign in to Strand to load buyer walks.";

        private static readonly Dictionary<string, string> _buyerWalkSortPaths = new()
        {
            { "Job",         nameof(StrandBuyerWalk.JobNumber) },
            { "Address",     nameof(StrandBuyerWalk.Address) },
            { "Subdivision", nameof(StrandBuyerWalk.Subdivision) },
            { "City",        nameof(StrandBuyerWalk.City) },
            { "Ready",       nameof(StrandBuyerWalk.ReadyDate) },
            { "Closing",     nameof(StrandBuyerWalk.ClosingDate) },
            { "Inspector",   nameof(StrandBuyerWalk.InspectorName) },
            { "1st",         nameof(StrandBuyerWalk.ScheduleDate) },
            { "2nd",         nameof(StrandBuyerWalk.Schedule2Date) },
            { "Note",        nameof(StrandBuyerWalk.Note) },
        };

        private int _buyerWalkLoadVersion;
        private bool _buyerWalksBusy;
        private bool _buyerWalksLoadedOnce;
        private bool _buyerWalkDialogOpen;
        private string? _buyerWalkSortPath; // null = server order within each group (§5.5)
        private ListSortDirection _buyerWalkSortDirection = ListSortDirection.Ascending;
        private ListViewItem? _pressedBuyerWalkItem;
        private bool _pressedBuyerWalkInSelectColumn;
        private readonly HashSet<CheckBox> _buyerWalkGroupCheckBoxes = new();

        private void ShowBuyerWalkTab()
        {
            if (_profileLocked)
            {
                ShowSettingsTab();
                return;
            }
            MyListPanel.Visibility = Visibility.Collapsed;
            SettingsPanel.Visibility = Visibility.Collapsed;
            OrdersPanel.Visibility = Visibility.Collapsed;
            BuyerWalkPanel.Visibility = Visibility.Visible;
            AboutPanel.Visibility = Visibility.Collapsed;
            SetActiveTab(BuyerWalkTabButton);
            UpdateBuyerWalkSignInUi();

            // §4 entry points (as OC §4.8): signed in → load every time it is shown; signed out and not signed out by
            // hand → silent sign-in, prompting only on the first attempt.
            if (StrandScheduleService.IsSignedIn)
                _ = LoadBuyerWalksAsync();
            else if (!_buyerWalksBusy && !_ordersSignedOutManually)
                _ = SignInAndLoadBuyerWalksAsync(trySavedLogin: true, promptIfNeeded: !_buyerWalksLoadedOnce);
        }

        private async void RefreshBuyerWalksButton_Click(object sender, RoutedEventArgs e)
        {
            if (StrandScheduleService.IsSignedIn)
                await LoadBuyerWalksAsync();
            else
                await SignInAndLoadBuyerWalksAsync(trySavedLogin: true, promptIfNeeded: true);
        }

        private async void BuyerWalkSignInButton_Click(object sender, RoutedEventArgs e)
        {
            if (StrandScheduleService.IsSignedIn)
            {
                EndStrandSession("Signed out of Strand.");
                OfflineCacheService.RefuseSavedLoginForThisRun(); // F-020 R2a: an explicit sign-out; no download on the saved login until the next sign-in
                _ordersSignedOutManually = true;
                return;
            }
            await SignInAndLoadBuyerWalksAsync(trySavedLogin: false, promptIfNeeded: true);
        }

        // §4: Sign Out, Clear, an account change or a session sync clears the rows.
        private void ClearBuyerWalks()
        {
            _buyerWalkLoadVersion++;
            BuyerWalkListView.ItemsSource = null;
            BuyerWalkStatusText.Text = StrandScheduleService.IsSignedIn ? "" : BuyerWalkSignInPrompt;
            UpdateBuyerWalkSignInUi();
            UpdateBuyerWalkSelectionUi();
        }

        private async Task SignInAndLoadBuyerWalksAsync(bool trySavedLogin, bool promptIfNeeded)
        {
            if (_buyerWalksBusy || _ordersBusy)
                return;
            _buyerWalksLoadedOnce = true;
            bool triedSaved = false;
            if (trySavedLogin)
            {
                SetBuyerWalksBusy(true, null);
                (bool SignedIn, bool Tried) result;
                try { result = await TrySilentStrandSignInAsync(); }
                finally { SetBuyerWalksBusy(false, null); }
                if (result.SignedIn)
                {
                    _ordersSignedOutManually = false;
                    await LoadBuyerWalksAsync();
                    return;
                }
                triedSaved = result.Tried;
                if (triedSaved) BuyerWalkStatusText.Text = OrdersStatusText.Text; // "Strand sign-in failed for …"
            }
            if (_pickerClosed)
                return;
            if (trySavedLogin && (BuyerWalkPanel.Visibility != Visibility.Visible || !IsActive))
                promptIfNeeded = false;
            if (!promptIfNeeded)
            {
                if (!triedSaved && !StrandScheduleService.IsSignedIn)
                    BuyerWalkStatusText.Text = BuyerWalkSignInPrompt;
                return;
            }

            string lastUsername = _lastStrandUsername.Length > 0 ? _lastStrandUsername : StrandCredentialService.GetEffective()?.Username ?? "";
            var signIn = new StrandSignInWindow(lastUsername) { Owner = this };
            _buyerWalkDialogOpen = true;
            bool? ok;
            try { ok = signIn.ShowDialog(); }
            finally { _buyerWalkDialogOpen = false; }
            if (ok != true)
            {
                if (!triedSaved)
                    BuyerWalkStatusText.Text = BuyerWalkSignInPrompt;
                UpdateBuyerWalkSignInUi();
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
            {
                BuyerWalkStatusText.Text = OrdersStatusText.Text;
                UpdateBuyerWalkSignInUi();
                return;
            }
            _ordersSignedOutManually = false;
            await LoadBuyerWalksAsync();
        }

        // L1/L7 (§7): B1, replace the rows, clear the selection; a failure keeps the previous rows.
        private async Task<bool> LoadBuyerWalksAsync()
        {
            if (_buyerWalksBusy || _pickerClosed)
                return false;
            int loadVersion = ++_buyerWalkLoadVersion;
            int generation = StrandScheduleService.SessionGeneration;
            SetBuyerWalksBusy(true, "Loading buyer walks from Strand...");
            try
            {
                var rows = await StrandBuyerWalkService.Default.GetBuyerWalksAsync();
                if (loadVersion != _buyerWalkLoadVersion || _pickerClosed || generation != StrandScheduleService.SessionGeneration)
                    return false;
                double verticalOffset = GetScrollViewer(BuyerWalkListView)?.VerticalOffset ?? 0;
                BuyerWalkListView.ItemsSource = rows; // a new list: the selection is cleared (§5.7)
                ApplyBuyerWalkView();
                UpdateBuyerWalkFilterUi();
                BuyerWalkStatusText.Text = StrandBuyerWalkService.LoadedText(rows.Count, DateTime.Now);
                _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () => GetScrollViewer(BuyerWalkListView)?.ScrollToVerticalOffset(verticalOffset));
                return true;
            }
            catch (StrandAuthRequiredException ex)
            {
                if (loadVersion == _buyerWalkLoadVersion)
                {
                    BuyerWalkListView.ItemsSource = null;
                    BuyerWalkStatusText.Text = ex.Message;
                }
                return false;
            }
            catch (Exception ex)
            {
                if (loadVersion == _buyerWalkLoadVersion)
                    BuyerWalkStatusText.Text = StrandBuyerWalkService.LoadFailedText(ex.Message); // BW-D01: rows kept
                return false;
            }
            finally
            {
                SetBuyerWalksBusy(false, null);
                UpdateBuyerWalkSignInUi();
                UpdateBuyerWalkSelectionUi();
            }
        }

        private void ApplyBuyerWalkView()
        {
            var view = CollectionViewSource.GetDefaultView(BuyerWalkListView.ItemsSource);
            if (view == null)
                return;
            using (view.DeferRefresh())
            {
                view.Filter = BuyerWalkFilterPredicate;
                view.GroupDescriptions.Clear();
                view.SortDescriptions.Clear();
                view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(StrandBuyerWalk.Group)));
                view.SortDescriptions.Add(new SortDescription(nameof(StrandBuyerWalk.Group), ListSortDirection.Ascending));
                if (_buyerWalkSortPath != null)
                    view.SortDescriptions.Add(new SortDescription(_buyerWalkSortPath, _buyerWalkSortDirection));
                view.SortDescriptions.Add(new SortDescription(nameof(StrandBuyerWalk.ServerIndex), ListSortDirection.Ascending)); // equal keys keep server order
            }
        }

        private void SetBuyerWalksBusy(bool busy, string? message)
        {
            _buyerWalksBusy = busy;
            if (message != null)
                BuyerWalkStatusText.Text = message;
            RefreshBuyerWalksButton.IsEnabled = !busy;
            BuyerWalkSignInButton.IsEnabled = !busy;
            BuyerWalkListView.Cursor = busy ? Cursors.Wait : null;
            UpdateBuyerWalkSelectionUi();
        }

        private void UpdateBuyerWalkSignInUi()
        {
            BuyerWalkSignInButton.Content = StrandScheduleService.IsSignedIn ? "Sign Out" : "Sign In";
            BuyerWalkSignedInText.Text = StrandScheduleService.IsSignedIn ? $"Signed in as {StrandScheduleService.DisplayName}" : "Not signed in";
        }

        // §8.1: Open and Add to My List need a checked row, a session and no work in flight.
        private void UpdateBuyerWalkSelectionUi()
        {
            int count = BuyerWalkListView.SelectedItems.Count;
            bool canAct = !_buyerWalksBusy && StrandScheduleService.IsSignedIn;
            BuyerWalkOpenButton.IsEnabled = canAct && count > 0;
            BuyerWalkAddToMyListButton.IsEnabled = canAct && count > 0;
            BuyerWalkSelectionText.Text = count == 0 ? "" : $"{count} selected";
            foreach (var box in _buyerWalkGroupCheckBoxes)
                UpdateBuyerWalkGroupCheckBox(box);
            UpdateBuyerWalkFilterUi();
        }

        // Checked rows in server order (§5.7).
        private List<StrandBuyerWalk> CheckedBuyerWalks() =>
            BuyerWalkListView.SelectedItems.OfType<StrandBuyerWalk>().OrderBy(r => r.ServerIndex).ToList();

        private void BuyerWalkListView_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateBuyerWalkSelectionUi();

        private void BuyerWalkGroupCheckBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox box)
            {
                _buyerWalkGroupCheckBoxes.Add(box);
                UpdateBuyerWalkGroupCheckBox(box);
            }
        }

        private void BuyerWalkGroupCheckBox_Unloaded(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox box)
                _buyerWalkGroupCheckBoxes.Remove(box);
        }

        // OC §5.7: select or clear every visible row of the group.
        private void BuyerWalkGroupCheckBox_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not CheckBox { DataContext: CollectionViewGroup group } box)
                return;
            var rows = group.Items.OfType<StrandBuyerWalk>().ToList();
            var selected = BuyerWalkListView.SelectedItems.OfType<StrandBuyerWalk>().ToHashSet();
            bool selectAll = rows.Any(r => !selected.Contains(r));
            foreach (var row in rows)
            {
                if (selectAll && !selected.Contains(row)) BuyerWalkListView.SelectedItems.Add(row);
                else if (!selectAll && selected.Contains(row)) BuyerWalkListView.SelectedItems.Remove(row);
            }
            UpdateBuyerWalkGroupCheckBox(box);
        }

        private void UpdateBuyerWalkGroupCheckBox(CheckBox box)
        {
            if (box.DataContext is not CollectionViewGroup group)
                return;
            var rows = group.Items.OfType<StrandBuyerWalk>().ToList();
            var selected = BuyerWalkListView.SelectedItems.OfType<StrandBuyerWalk>().ToHashSet();
            box.IsChecked = rows.Count > 0 && rows.All(selected.Contains);
        }

        private bool IsInBuyerWalkSelectColumn(ListViewItem item, MouseButtonEventArgs e) =>
            BuyerWalkListView.View is GridView { Columns.Count: > 0 } view && e.GetPosition(item).X < view.Columns[0].ActualWidth;

        private void BuyerWalkRow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not ListViewItem item)
                return;
            _pressedBuyerWalkItem = item;
            _pressedBuyerWalkInSelectColumn = IsInBuyerWalkSelectColumn(item, e);
            e.Handled = true;
        }

        // L5 (INSPECT, BW §7.1): the checkbox cell toggles; elsewhere, toggle when any row is checked, else open.
        private async void BuyerWalkRow_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            var pressed = _pressedBuyerWalkItem;
            _pressedBuyerWalkItem = null;
            if (sender is not ListViewItem { DataContext: StrandBuyerWalk row } item)
                return;
            e.Handled = true;
            if (!ReferenceEquals(item, pressed))
                return;
            if (_pressedBuyerWalkInSelectColumn || BuyerWalkListView.SelectedItems.Count > 0)
                item.IsSelected = !item.IsSelected;
            else
                await OpenBuyerWalkAsync(row);
        }

        private async void BuyerWalkOpenButton_Click(object sender, RoutedEventArgs e)
        {
            if (CheckedBuyerWalks().FirstOrDefault() is StrandBuyerWalk first)
                await OpenBuyerWalkAsync(first);
        }

        private void BuyerWalkColumnHeader_Click(object sender, RoutedEventArgs e)
        {
            if (e.OriginalSource is not GridViewColumnHeader { Column: not null } header ||
                header.Column.Header is not string headerText ||
                !_buyerWalkSortPaths.TryGetValue(headerText, out string? path))
                return;
            // OC §5.8 tap rules: the column sorted ascending switches to descending; any other tap sorts ascending.
            _buyerWalkSortDirection = path == _buyerWalkSortPath && _buyerWalkSortDirection == ListSortDirection.Ascending
                ? ListSortDirection.Descending : ListSortDirection.Ascending;
            _buyerWalkSortPath = path;
            ApplyBuyerWalkView();
        }

        // E1: B2, then the Edit Schedule sheet; a failed read opens no sheet (BW-D03).
        private async Task OpenBuyerWalkAsync(StrandBuyerWalk row)
        {
            if (_buyerWalksBusy || _pickerClosed)
                return;
            if (!StrandScheduleService.IsSignedIn)
            {
                await SignInAndLoadBuyerWalksAsync(trySavedLogin: true, promptIfNeeded: true);
                return;
            }
            string job = row.JobNumber ?? "";
            int generation = StrandScheduleService.SessionGeneration;
            StrandBuyerWalkEdit edit;
            SetBuyerWalksBusy(true, null);
            try { edit = await StrandBuyerWalkService.Default.ReadAsync(job); }
            catch (StrandAuthRequiredException ex)
            {
                ShowBuyerWalkAlert(ex.Message, MessageBoxImage.Warning);
                return;
            }
            catch (Exception ex)
            {
                ShowBuyerWalkAlert(StrandBuyerWalkService.ReadFailureText(job, ex), MessageBoxImage.Error);
                return;
            }
            finally { SetBuyerWalksBusy(false, null); }
            if (_pickerClosed || generation != StrandScheduleService.SessionGeneration || !StrandScheduleService.IsSignedIn)
                return;

            var sheet = new BuyerWalkSheet(edit, row.IsReinspection);
            var window = new BuyerWalkScheduleWindow(sheet, row.Address ?? edit.Address ?? "", StrandBuyerWalkService.Default) { Owner = this };
            window.AddToMyListRequested = () => AddBuyerWalksFromSheetAsync(window, sheet);
            _buyerWalkDialogOpen = true;
            try { window.ShowDialog(); }
            finally { _buyerWalkDialogOpen = false; }
            // E10 / BW-D09: reload after a successful Save or a sheet Add to My List; Cancel reloads nothing (E9).
            if (window.Saved && !_pickerClosed)
                await LoadBuyerWalksAsync();
        }

        private void ShowBuyerWalkAlert(string text, MessageBoxImage icon)
        {
            if (_pickerClosed)
                return;
            _buyerWalkDialogOpen = true;
            try { MessageBox.Show(this, text, "", MessageBoxButton.OK, icon); } // INSPECT's alerts have no title
            finally { _buyerWalkDialogOpen = false; }
        }
    }
}
