using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using InspectionEditor.Services;

namespace InspectionEditor
{
    // Settings > Strand Account, and silent sign-in for the ORDERS tab.
    public partial class InspectionPickerWindow
    {
        private string _strandAccountSource = "";
        // Bumped by Clear and Sign Out so a sign-in or load already in flight cannot undo them.
        private int _strandSessionVersion;
        // The login last shown on the card; unsaved edits are kept while the stored login is unchanged.
        private (string User, string Pass)? _strandCardShown;

        private bool _strandTestSaveRunning;

        private void LoadStrandAccountUi()
        {
            // The fee token is shown once: opening Settings again starts with an empty box.
            ClearFeeToken();
            UpdateFeeTokenUi();

            // Leave the fields and the "Testing..." status alone while Test & Save runs.
            if (_strandTestSaveRunning)
                return;

            var saved = StrandCredentialService.LoadSaved();
            var credentials = saved ?? StrandCredentialService.ImportFromInspect();
            var stored = (credentials?.Username ?? "", credentials?.Password ?? "");
            bool hasUnsavedEdits = _strandCardShown is { } shown &&
                (StrandUsernameBox.Text != shown.User || StrandPasswordBox.Password != shown.Pass);
            if (hasUnsavedEdits && _strandCardShown == stored)
                return;

            ShowStrandCredentials(credentials);
            StrandAccountStatusText.Text = credentials == null
                ? "Not set. Enter your Strand login, or set it in Inspect first."
                : saved != null
                    ? StrandScheduleService.IsSignedIn ? $"Saved. Signed in as {StrandScheduleService.DisplayName}." : "Saved in RED."
                    : $"Found in {credentials.Source} settings. Click Test & Save to use it.";
        }

        private void ShowStrandCredentials(StrandCredentials? credentials)
        {
            StrandUsernameBox.Text = credentials?.Username ?? "";
            StrandPasswordBox.Password = credentials?.Password ?? "";
            _strandAccountSource = credentials?.Source ?? "";
            _strandCardShown = (credentials?.Username ?? "", credentials?.Password ?? "");
        }

        private async void StrandTestSaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (_ordersBusy)
            {
                StrandAccountStatusText.Text = "Strand is busy on the ORDERS tab; try again in a moment.";
                return;
            }

            var credentials = new StrandCredentials
            {
                Username = StrandUsernameBox.Text.Trim(),
                Password = StrandPasswordBox.Password,
                ApiUrl = StrandCredentialService.ImportFromInspect()?.ApiUrl ?? "",
                Source = string.IsNullOrEmpty(_strandAccountSource) ? "RED" : _strandAccountSource
            };
            if (!credentials.IsComplete)
            {
                StrandAccountStatusText.Text = "Enter both the username and the password.";
                return;
            }

            int session = ++_strandSessionVersion;
            _ordersLoadVersion++; // a load started under the previous login must not fill the grid
            StrandAccountStatusText.Text = "Testing connection to Strand...";
            string previousOrdersStatus = OrdersStatusText.Text;
            string previousUser = StrandScheduleService.Username;
            int generationBefore = StrandScheduleService.SessionGeneration;
            SetOrdersBusy(true, "Signing in to Strand...");
            _strandTestSaveRunning = true;
            bool signedIn = false;
            bool sessionEnded = false;
            try
            {
                StrandScheduleService.UseApiUrl(credentials.ApiUrl);
                await StrandScheduleService.SignInAsync(credentials.Username, credentials.Password);
                if (session != _strandSessionVersion)
                {
                    StrandScheduleService.SignOut();
                    return;
                }

                signedIn = true;
                _lastStrandUsername = credentials.Username;
                _ordersSignedOutManually = false;
                _ordersSessionGeneration = StrandScheduleService.SessionGeneration;
                // A different account must not act on the previous account's rows if the reload fails.
                if (!string.Equals(previousUser, StrandScheduleService.Username, StringComparison.OrdinalIgnoreCase))
                {
                    OrdersListView.ItemsSource = null;
                    OfflineCacheService.DeleteJobsCache(); // F-020 C2: the previous account's jobs list goes with it
                    ClearBuyerWalks();                     // F-025 §4: an account change clears the rows
                }
                try
                {
                    StrandCredentialService.Save(credentials);
                    _strandCardShown = (credentials.Username, credentials.Password);
                    StrandAccountStatusText.Text = $"Connected and saved. Signed in as {StrandScheduleService.DisplayName}.";
                }
                catch (Exception saveEx)
                {
                    DiagnosticLogService.Log("Could not save the Strand login", saveEx);
                    StrandAccountStatusText.Text = $"Signed in as {StrandScheduleService.DisplayName}, but the login could not be saved: {saveEx.Message}";
                }
            }
            catch (Exception ex) when (session == _strandSessionVersion &&
                                       StrandScheduleService.SessionGeneration == generationBefore && // nothing was signed in
                                       ex is HttpRequestException { StatusCode: null } or TaskCanceledException)
            {
                // No connection (not a rejected password): keep the login so the profile can be completed offline;
                // ORDERS signs in with it once Strand can be reached.
                try
                {
                    StrandCredentialService.Save(credentials);
                    _strandCardShown = (credentials.Username, credentials.Password);
                    StrandAccountStatusText.Text = "Saved without testing: RED could not reach Strand. ORDERS will sign in when you are online.";
                    // Another account's session must not keep acting in ORDERS under the newly saved login.
                    if (StrandScheduleService.IsSignedIn &&
                        !string.Equals(previousUser, credentials.Username, StringComparison.OrdinalIgnoreCase))
                    {
                        EndStrandSession(OrdersSignInPrompt);
                        sessionEnded = true;
                    }
                    _ordersSignedOutManually = false;
                    _ordersLoadedOnce = false; _buyerWalksLoadedOnce = false;
                    _lastStrandUsername = "";
                }
                catch (Exception saveEx)
                {
                    DiagnosticLogService.Log("Could not save the Strand login", saveEx);
                    StrandAccountStatusText.Text = $"Could not reach Strand, and the login could not be saved: {saveEx.Message}";
                }
            }
            catch (Exception ex)
            {
                if (session == _strandSessionVersion)
                    StrandAccountStatusText.Text = $"Could not sign in: {ex.Message}";
            }
            finally
            {
                _strandTestSaveRunning = false;
                // A failed test leaves the session as it was, so the earlier ORDERS status still applies.
                SetOrdersBusy(false, signedIn ? "" : sessionEnded ? OrdersSignInPrompt : previousOrdersStatus);
                UpdateOrdersSignInUi();
                UpdateProfileLock(); // the Strand login is part of the required profile
            }

            if (signedIn)
                await LoadOrdersAsync();
        }

        private void StrandClearButton_Click(object sender, RoutedEventArgs e)
        {
            StrandCredentialService.Clear();
            EndStrandSession(OrdersSignInPrompt);
            _ordersSignedOutManually = false;
            _ordersLoadedOnce = false; _buyerWalksLoadedOnce = false;
            _lastStrandUsername = "";
            ShowStrandCredentials(null);
            StrandAccountStatusText.Text = "Cleared. Enter your Strand login and press Test & Save, or press Import from INSPECT.";
            UpdateProfileLock(); // the Strand login is part of the required profile
        }

        // iPad fee token (ORDERS D-03 = B): super user only, shown once, never saved or logged.
        private void UpdateFeeTokenUi()
        {
            bool owner = StrandScheduleService.IsSuperUser;
            FeeTokenPanel.Visibility = owner ? Visibility.Visible : Visibility.Collapsed;
            if (!owner)
                ClearFeeToken();
        }

        private void ClearFeeToken()
        {
            FeeTokenBox.Clear();
            FeeTokenCopyButton.IsEnabled = false;
        }

        private void FeeTokenMakeButton_Click(object sender, RoutedEventArgs e)
        {
            if (!StrandScheduleService.IsSuperUser)
            {
                ClearFeeToken();
                return;
            }

            FeeTokenBox.Text = StrandMyListService.CreateFeeToken();
            FeeTokenCopyButton.IsEnabled = true;
            FeeTokenBox.Focus();
            FeeTokenBox.SelectAll();
        }

        private void FeeTokenCopyButton_Click(object sender, RoutedEventArgs e)
        {
            if (!StrandScheduleService.IsSuperUser || FeeTokenBox.Text.Length == 0)
                return;
            try
            {
                Clipboard.SetText(FeeTokenBox.Text);
                StrandAccountStatusText.Text = "Fee token copied. Paste it into the iPad.";
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // Another app is holding the clipboard; the token is still selectable in the box.
                StrandAccountStatusText.Text = "Could not copy (the clipboard is busy). Select the token and press Ctrl+C.";
            }
        }

        // Tries RED's saved login. Inspect's login is used when nothing is saved, or as a fallback
        // for the same user (password changed in Inspect); it never replaces a different saved account.
        private async Task<(bool SignedIn, bool Tried)> TrySilentStrandSignInAsync()
        {
            var candidates = new List<StrandCredentials>();
            var saved = StrandCredentialService.LoadSaved();
            if (saved is { IsComplete: true })
                candidates.Add(saved);
            if (StrandCredentialService.ImportFromInspect() is { IsComplete: true } imported &&
                (saved == null || string.Equals(imported.Username, saved.Username, StringComparison.OrdinalIgnoreCase)) &&
                !candidates.Exists(c => c.Password == imported.Password))
            {
                candidates.Add(imported);
            }

            int session = _strandSessionVersion;
            foreach (var credentials in candidates)
            {
                if (session != _strandSessionVersion)
                    break;
                if (await TryStrandSignInAsync(credentials))
                    return (true, true);
            }

            return (false, candidates.Count > 0);
        }

        private async Task<bool> TryStrandSignInAsync(StrandCredentials credentials)
        {
            int session = _strandSessionVersion;
            SetOrdersBusy(true, $"Signing in to Strand as {credentials.Username}...");
            try
            {
                StrandScheduleService.UseApiUrl(credentials.ApiUrl);
                await StrandScheduleService.SignInAsync(credentials.Username, credentials.Password);
                if (session != _strandSessionVersion)
                {
                    // Cleared or signed out while this was running: drop the new token, save nothing.
                    StrandScheduleService.SignOut();
                    return false;
                }

                _lastStrandUsername = credentials.Username;
                _ordersSessionGeneration = StrandScheduleService.SessionGeneration;
                try { StrandCredentialService.Save(credentials); }
                catch (Exception ex) { DiagnosticLogService.Log("Could not save the Strand login", ex); }
                return true;
            }
            catch (Exception ex)
            {
                if (session == _strandSessionVersion)
                    OrdersStatusText.Text = $"Strand sign-in failed for {credentials.Username}: {ex.Message}";
                return false;
            }
            finally
            {
                SetOrdersBusy(false);
                UpdateOrdersSignInUi();
            }
        }
    }
}
