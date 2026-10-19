using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using InspectionEditor.Services;

namespace InspectionEditor
{
    // F-025 §10 Add to My List (INSPECT BuyerWalkService): gates, then per row A1 → A1b → W2 → A6 → duplicates → act,
    // alerts at once, INSPECT's summary. No assign, no RequestFiles, no reload; the selection stays (A4).
    public partial class InspectionPickerWindow
    {
        private async void BuyerWalkAddToMyListButton_Click(object sender, RoutedEventArgs e)
        {
            var rows = CheckedBuyerWalks().Select(r => new BuyerWalkAddRow
            {
                JobNumber = r.JobNumber ?? "",
                IsReinspection = r.IsReinspection,
                StatusId = r.ScheduleStatusId,
                Date = r.AddDate // Schedule2Date for a re-inspection row (§10.1)
            }).ToList();
            await AddBuyerWalksToMyListAsync(rows, this);
        }

        // §10.1 gates, before any server call: signed in, the F-003 O9 profile guard (UserType kind O10, initials O6),
        // a valid Inspect folder. Returns the profile, or null when a gate refused (its alert shown).
        private async Task<CompleteProfile?> BuyerWalkAddGatesAsync(Window owner)
        {
            if (!StrandScheduleService.IsSignedIn)
            {
                await SignInAndLoadBuyerWalksAsync(trySavedLogin: true, promptIfNeeded: true);
                return null;
            }
            _buyerWalkDialogOpen = true;
            CompleteProfile? profile;
            try { profile = ProfileGuard.Require(owner, "Add to My List"); }
            finally { _buyerWalkDialogOpen = false; }
            if (profile == null)
                return null;
            if (!Directory.Exists(profile.Folder))
            {
                MessageBox.Show(owner, $"Could not find your Inspections folder ({profile.Folder}). Check it in Settings > Inspector Profile.", "", MessageBoxButton.OK, MessageBoxImage.Error);
                return null;
            }
            return profile;
        }

        // Returns the number copied, or null when nothing ran.
        private async Task<int?> AddBuyerWalksToMyListAsync(List<BuyerWalkAddRow> rows, Window owner, CompleteProfile? checkedProfile = null)
        {
            if (rows.Count == 0 || _buyerWalksBusy || _pickerClosed)
                return null;
            var profile = checkedProfile ?? await BuyerWalkAddGatesAsync(owner);
            if (profile == null || _pickerClosed)
                return null;

            var adder = new BuyerWalkAdder
            {
                Service = StrandBuyerWalkService.Default,
                InspectorFolder = profile.Folder,
                UserKind = profile.Kind,
                InitialsName = profile.InspectorName, // F-003 O6: never the Strand DisplayName
                AskYesNo = question => Ask(owner, question),
                Error = message => Alert(owner, message, MessageBoxImage.Error),
                IsOpenInRed = IsOpenInRed
            };
            int copied = 0;
            string previousStatus = BuyerWalkStatusText.Text;
            SetBuyerWalksBusy(true, "Adding to My List...");
            try
            {
                copied = await adder.AddAsync(rows);
            }
            catch (StrandAuthRequiredException ex)
            {
                Alert(owner, ex.Message, MessageBoxImage.Warning); // an auth error stops the batch
            }
            finally
            {
                SetBuyerWalksBusy(false, previousStatus);
                UpdateBuyerWalkSignInUi();
            }
            if (_pickerClosed)
                return copied;
            RefreshMyListIfShowing(profile.Folder);
            if (copied > 0)
                Alert(owner, BuyerWalkAdder.Summary(copied), MessageBoxImage.Information);
            return copied;
        }

        // A5: gates, Save (E3 + B3), then the grid sequence for this job with the sheet's status and Scheduled value.
        private async Task AddBuyerWalksFromSheetAsync(BuyerWalkScheduleWindow window, BuyerWalkSheet sheet)
        {
            var profile = await BuyerWalkAddGatesAsync(window);
            if (profile == null)
                return;
            if (!await window.SaveAsync(partOfAdd: true))
                return; // its alert showed; nothing else is sent
            var row = new BuyerWalkAddRow
            {
                JobNumber = sheet.Loaded.JobNumber ?? "",
                IsReinspection = sheet.IsReinspection,
                StatusId = sheet.StatusId ?? sheet.Loaded.ScheduleStatusId,
                Date = sheet.Scheduled // INSPECT uses the sheet's single Scheduled box, even for a re-inspection (§14)
            };
            int? copied = await AddBuyerWalksToMyListAsync(new List<BuyerWalkAddRow> { row }, window, profile);
            if (copied > 0)
                window.CloseAfterAdd(); // "Copied buyer walk to My List." already shown; the list reloads as it closes
        }

        private bool IsOpenInRed(string path)
        {
            CaptureOpenInspectionLocks();
            return _openFilePaths.Contains(Path.GetFullPath(path)) ||
                   Application.Current.Windows.OfType<MainWindow>().Any(w => w.HasOpenInspection && PathEquals(w.OpenInspectionFilePath, path));
        }

        private bool Ask(Window owner, string question)
        {
            _buyerWalkDialogOpen = true;
            try { return MessageBox.Show(owner, question, BuyerWalkAdder.Caption, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes; }
            finally { _buyerWalkDialogOpen = false; }
        }

        private void Alert(Window owner, string message, MessageBoxImage icon)
        {
            if (_pickerClosed)
                return;
            _buyerWalkDialogOpen = true;
            try { MessageBox.Show(owner, message, "", MessageBoxButton.OK, icon); } // INSPECT Error.Handle: no title
            finally { _buyerWalkDialogOpen = false; }
        }
    }
}
