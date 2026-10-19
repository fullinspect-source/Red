using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using InspectionEditor.Services;

namespace InspectionEditor
{
    // Settings > Inspector Profile (F-003) and the start-up lock (owner, 2026-10-18): while anything INSPECT's own
    // start-up check requires is missing, RED opens on Settings and My List and ORDERS stay locked.
    // The user type is shown read-only; it is changed in INSPECT and brought over with Import from INSPECT.
    public partial class InspectionPickerWindow
    {
        private bool _loadingProfileUi;
        private bool _profileLocked;
        private bool _profileWasLocked;

        private static readonly Brush ProfileErrorBrush = new SolidColorBrush(Color.FromRgb(180, 35, 24));
        private static readonly Brush ProfileNoteBrush = new SolidColorBrush(Color.FromRgb(82, 97, 111));

        // Startup: import INSPECT's values on the first run (O1), then open My List, or Settings while the profile is
        // incomplete or the first-run import has something to say (e.g. it used INSPECT's backup file, E1).
        private void ShowStartTab()
        {
            InspectorProfileService.EnsureImportedOnFirstRun();
            _lastSeenInspectionsFolder = InspectorProfileService.Load()?.InspectionsFolder;
            UpdateProfileLock();
            if (_profileLocked || InspectorProfileService.FirstRunMessage != null)
                ShowSettingsTab();
            else
                ShowMyListTab();
        }

        // Checks RED's profile and saved Strand login, locks or unlocks My List and ORDERS, and updates the banner.
        // If the profile became incomplete while My List or ORDERS was showing, Settings takes over.
        private void UpdateProfileLock()
        {
            bool wasLocked = _profileLocked;
            var missing = InspectorProfileService.MissingItems(InspectorProfileService.Load(), StrandCredentialService.LoadSaved());
            _profileLocked = missing.Count > 0;
            _profileWasLocked |= _profileLocked;
            if (wasLocked && !_profileLocked)
                RepointMyListToProfile();

            MyListTabButton.IsEnabled = !_profileLocked;
            OrdersTabButton.IsEnabled = !_profileLocked;
            object? tip = _profileLocked ? InspectorProfileService.MissingProfileMessage : null;
            MyListTabButton.ToolTip = tip;
            OrdersTabButton.ToolTip = tip;

            if (_profileLocked)
            {
                ProfileBanner.Visibility = Visibility.Visible;
                ProfileBanner.Background = new SolidColorBrush(Color.FromRgb(254, 243, 242));
                ProfileBanner.BorderBrush = new SolidColorBrush(Color.FromRgb(240, 68, 56));
                ProfileBannerTitle.Foreground = ProfileErrorBrush;
                ProfileBannerTitle.Text = InspectorProfileService.MissingProfileMessage;
                ProfileBannerDetail.Text = "Missing: " + string.Join("; ", missing) + ".";
                ProfileBannerDetail.Visibility = Visibility.Visible;
                ProfileBannerImportButton.Visibility = Visibility.Visible;
            }
            else if (_profileWasLocked)
            {
                ProfileBanner.Visibility = Visibility.Visible;
                ProfileBanner.Background = new SolidColorBrush(Color.FromRgb(236, 253, 243));
                ProfileBanner.BorderBrush = new SolidColorBrush(Color.FromRgb(50, 213, 131));
                ProfileBannerTitle.Foreground = new SolidColorBrush(Color.FromRgb(2, 122, 72));
                ProfileBannerTitle.Text = InspectorProfileService.CompleteMessage;
                ProfileBannerDetail.Visibility = Visibility.Collapsed;
                ProfileBannerImportButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                ProfileBanner.Visibility = Visibility.Collapsed;
            }

            if (_profileLocked && (MyListPanel.Visibility == Visibility.Visible || OrdersPanel.Visibility == Visibility.Visible))
                ShowSettingsTab();
        }

        // Fills the card from RED's profile file. A box whose text already matches is left alone, so reloading
        // (e.g. on window activation) never moves the caret.
        private void LoadInspectorProfileUi()
        {
            var profile = InspectorProfileService.Load();
            _loadingProfileUi = true;
            try
            {
                ProfileStateCombo.ItemsSource ??= InspectorProfileService.States;
                SetIfDifferent(ProfileNameBox, profile?.InspectorName);
                SetIfDifferent(ProfilePhoneBox, profile?.Phone);
                ProfileStateCombo.SelectedItem = InspectorProfileService.States.FirstOrDefault(state =>
                    string.Equals(state, profile?.DefaultState?.Trim(), StringComparison.OrdinalIgnoreCase));
                SetIfDifferent(ProfileFolderBox, profile?.InspectionsFolder);
            }
            finally
            {
                _loadingProfileUi = false;
            }

            static void SetIfDifferent(TextBox box, string? value)
            {
                if (box.Text.Trim() != (value ?? ""))
                    box.Text = value ?? "";
            }

            ShowProfileDetails(profile);
            ShowArchiveRetention(profile?.InspectionsFolder);
            if (InspectorProfileService.FirstRunMessage is { } message)
            {
                ProfileStatusText.Text = message;
                InspectorProfileService.ClearFirstRunMessage();
            }
            UpdateProfileLock();
        }

        private void ShowProfileDetails(InspectorProfile? profile)
        {
            ProfileImportedText.Text = profile?.ImportedAt is { } importedAt
                ? $"Imported from INSPECT on {importedAt:M/d/yyyy h:mm tt}. Changes you make here are saved right away."
                : "Not imported from INSPECT yet. Press Import from INSPECT, or fill in the fields.";

            ProfileNameError.Text = InspectorProfileService.ValidateName(ProfileNameBox.Text) ?? "";
            ProfilePhoneError.Text = InspectorProfileService.ValidatePhone(ProfilePhoneBox.Text) ?? "";
            ProfileStateError.Text = InspectorProfileService.ValidateState(ProfileStateCombo.SelectedItem as string) ?? "";
            ProfileFolderError.Text = InspectorProfileService.ValidateFolder(ProfileFolderBox.Text) ?? "";

            var kind = InspectUserType.Classify(profile?.UserType);
            InspectUserTypeText.Text = kind == InspectUserKind.Missing ? "(not set)" : profile!.UserType;
            InspectUserTypeKindText.Text = InspectUserType.KindLine(kind);
            InspectUserTypeKindText.Foreground = kind == InspectUserKind.Missing ? ProfileErrorBrush : ProfileNoteBrush;
            InspectUserTypeHintRun.Text = InspectUserType.ChangeHint;

            ProfileOtherText.Text = profile == null ? "" :
                "Also kept from INSPECT for later RED features: " +
                $"number pad {(profile.UseNumberPad ? "on" : "off")}, PDF editor {InspectorProfileService.PdfName(profile.PdfAppProgId)}, " +
                $"report viewer (defects only {YesNo(profile.ReportViewerDefectsOnly)}, pictures {YesNo(profile.ReportViewerPictures)}, " +
                $"attachments {YesNo(profile.ReportViewerAttachments)}), sketch pen {profile.SketchPenColor}, " +
                $"signature {(profile.Signature.Length > 0 ? "saved" : "none")}.";

            static string YesNo(bool value) => value ? "yes" : "no";
        }

        // F-015 N1/N4: INSPECT's Archive retention, read live from the folder's config.json (never imported or stored),
        // with the N2 message when it is 0 or negative.
        private void ShowArchiveRetention(string? inspectionsFolder)
        {
            int days = InspectLiveSettings.ReadArchiveRetention(inspectionsFolder);
            ArchiveRetentionText.Text = InspectLiveSettings.ArchiveRetentionLine(days, DateTime.Today);
            string? refusal = InspectLiveSettings.ArchiveRetentionRefusal(days);
            ArchiveRetentionRefusalText.Text = refusal ?? "";
            ArchiveRetentionRefusalText.Visibility = refusal == null ? Visibility.Collapsed : Visibility.Visible;
        }

        internal bool IsHomeWindow => _stayOpenHome;

        // Brings this window forward on Settings (ProfileGuard, F-003 O9).
        internal void OpenSettingsTab()
        {
            if (_pickerClosed)
            {
                App.ShowHomeSettings();
                return;
            }
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            Activate();
            ShowSettingsTab();
        }

        private void ProfileText_Changed(object sender, TextChangedEventArgs e) => SaveProfileFromUi(sender);

        private void ProfileState_Changed(object sender, SelectionChangedEventArgs e) => SaveProfileFromUi(sender);

        // Every edit is saved at once (O3). Only the edited field is written onto the file's current values, so
        // another RED window's newer values are never overwritten with this window's older ones.
        private void SaveProfileFromUi(object sender)
        {
            if (_loadingProfileUi || !IsLoaded)
                return;

            var profile = InspectorProfileService.Load() ?? new InspectorProfile();
            string oldFolder = profile.InspectionsFolder;
            if (ReferenceEquals(sender, ProfileNameBox))
                profile.InspectorName = ProfileNameBox.Text.Trim();
            else if (ReferenceEquals(sender, ProfilePhoneBox))
                profile.Phone = ProfilePhoneBox.Text.Trim();
            else if (ReferenceEquals(sender, ProfileStateCombo))
                profile.DefaultState = ProfileStateCombo.SelectedItem as string ?? "";
            else if (ReferenceEquals(sender, ProfileFolderBox))
                profile.InspectionsFolder = ProfileFolderBox.Text.Trim();
            else
                return;

            try
            {
                InspectorProfileService.Save(profile);
                ProfileStatusText.Text = "";
            }
            catch (Exception ex)
            {
                DiagnosticLogService.Log("Could not save the inspector profile", ex);
                ProfileStatusText.Text = $"Could not save the profile: {ex.Message}";
            }
            ShowProfileDetails(profile);
            UpdateProfileLock();
            if (!string.Equals(oldFolder, profile.InspectionsFolder, StringComparison.OrdinalIgnoreCase))
            {
                ShowArchiveRetention(profile.InspectionsFolder);
                RepointMyListToProfile();
            }
        }

        // My List follows the profile's Inspections folder (F-003 O7): when the folder shown is not inside it,
        // show {Inspections folder}\MyList, where Add to My List saves. A request made while a list is still loading
        // waits for that load to finish.
        private void RepointMyListToProfile()
        {
            string? folder = InspectorProfileService.Load()?.InspectionsFolder;
            if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
                return;
            _lastSeenInspectionsFolder = folder;
            if (_isLoading)
            {
                _pendingMyListRepoint = true;
                return;
            }
            if (!string.IsNullOrWhiteSpace(_currentFolderPath) && InspectorProfileService.IsInside(_currentFolderPath, folder))
                return;
            LoadInspections(Path.Combine(folder, "MyList")); // sets the folder at once, so SaveSettings remembers it
            SaveSettings();
        }

        private bool _pendingMyListRepoint;
        private string? _lastSeenInspectionsFolder;

        // Called by LoadInspections when it finishes.
        private void RunPendingMyListRepoint()
        {
            if (!_pendingMyListRepoint)
                return;
            _pendingMyListRepoint = false;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!_pickerClosed)
                    RepointMyListToProfile();
            }));
        }

        // On activation: another RED window may have moved the Inspections folder while the profile stayed complete.
        private void RepointIfProfileFolderChanged()
        {
            if (_profileLocked)
                return;
            string? folder = InspectorProfileService.Load()?.InspectionsFolder;
            if (!string.Equals(folder ?? "", _lastSeenInspectionsFolder ?? "", StringComparison.OrdinalIgnoreCase))
                RepointMyListToProfile();
        }

        private void ProfileFolderBrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Choose your Inspections folder" };
            if (Directory.Exists(ProfileFolderBox.Text))
                dialog.InitialDirectory = ProfileFolderBox.Text;
            if (dialog.ShowDialog(this) == true)
                ProfileFolderBox.Text = dialog.FolderName; // saved by ProfileText_Changed
        }

        // Full import from INSPECT on this PC (owner, 2026-10-18; F-003 O2): every value RED keeps, and the Strand
        // login. When RED already has a profile, each change is listed and applied only after the user agrees.
        private void ProfileImportButton_Click(object sender, RoutedEventArgs e)
        {
            if (_ordersBusy || _strandTestSaveRunning)
            {
                ShowSettingsTab();
                ProfileStatusText.Text = "Strand is busy; try Import from INSPECT again in a moment.";
                return;
            }

            var read = InspectorProfileService.ReadFromInspect(ProfileFolderBox.Text);
            if (read.Profile == null)
            {
                ShowSettingsTab();
                ProfileStatusText.Text = read.Message ?? InspectUserType.UnreadableMessage;
                return;
            }

            var current = InspectorProfileService.Load();
            var savedLogin = StrandCredentialService.LoadSaved();
            // Without a profile, only a different saved Strand login can be overwritten, so only that is listed.
            var changes = current != null ? InspectorProfileService.Diff(current, savedLogin, read)
                : savedLogin != null ? InspectorProfileService.Diff(read.Profile.Clone(), savedLogin, read)
                : null;
            if (changes is { Count: > 0 })
            {
                string question = (read.Message == null ? "" : read.Message + Environment.NewLine + Environment.NewLine) +
                                  "Import these values from INSPECT?" + Environment.NewLine + Environment.NewLine +
                                  string.Join(Environment.NewLine, changes);
                MessageBoxResult answer;
                _ordersDialogOpen = true;
                try { answer = MessageBox.Show(this, question, "Import from INSPECT", MessageBoxButton.YesNo, MessageBoxImage.Question); }
                finally { _ordersDialogOpen = false; }
                if (answer != MessageBoxResult.Yes)
                {
                    ProfileStatusText.Text = "Import cancelled. Nothing was changed.";
                    return;
                }
                if (_ordersBusy || _strandTestSaveRunning || _pickerClosed)
                {
                    ProfileStatusText.Text = "Strand got busy; nothing was changed. Try Import from INSPECT again in a moment.";
                    return;
                }
            }

            bool loginChanged = read.Login is { IsComplete: true } login &&
                                (savedLogin == null ||
                                 !string.Equals(savedLogin.Username, login.Username, StringComparison.OrdinalIgnoreCase) ||
                                 savedLogin.Password != login.Password);
            string? loginError;
            try
            {
                loginError = InspectorProfileService.Apply(read);
            }
            catch (Exception ex)
            {
                DiagnosticLogService.Log("Could not save the import from INSPECT", ex);
                ShowSettingsTab();
                ProfileStatusText.Text = $"Could not save the import: {ex.Message} Nothing was changed.";
                return;
            }

            if (loginChanged && loginError == null)
            {
                // The next ORDERS visit signs in with the imported login.
                EndStrandSession(OrdersSignInPrompt);
                _ordersSignedOutManually = false;
                _ordersLoadedOnce = false;
                _lastStrandUsername = "";
            }

            _strandCardShown = null; // show the login that is now saved
            ShowSettingsTab();
            RepointMyListToProfile();
            ProfileStatusText.Text = (current != null && changes is { Count: 0 } ? "Everything already matched INSPECT." : "Imported from INSPECT.") +
                                     (read.Message == null ? "" : " " + read.Message) +
                                     (loginError == null ? "" : " " + loginError);
        }
    }
}
