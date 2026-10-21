using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using InspectionEditor.Services;

namespace InspectionEditor
{
    // ORDERS > Add to My List, following Inspect's addFormButton_ItemClick / ServiceRequestManager.AddToMyList:
    // assign the requests to the signed-in user, then for each one fetch its Strand form and save it to My List.
    public partial class InspectionPickerWindow
    {
        private async void OrdersAddToMyListButton_Click(object sender, RoutedEventArgs e) =>
            await AddOrdersToMyListAsync(GetSelectedOrders());

        // skipRowChecks: the order dialog already validated fresh data; the grid row may be stale.
        // dialogProfile: the profile the order dialog already checked before its assign PATCH, so nothing read
        // again after that server write can refuse or disagree with it.
        // dialogItem: the request as the order dialog saved it; its date question was asked there, with the edited date.
        private async Task AddOrdersToMyListAsync(List<StrandScheduleRequest> selected, bool skipRowChecks = false,
                                                  CompleteProfile? dialogProfile = null, OrderAddItem? dialogItem = null)
        {
            if (selected.Count == 0 || _ordersBusy || _pickerClosed)
                return;
            if (!StrandScheduleService.IsSignedIn)
            {
                await SignInAndLoadOrdersAsync(trySavedLogin: true, promptIfNeeded: true);
                return;
            }

            // Same per-row checks as Inspect; a blocked row is reported and skipped.
            var rows = new List<StrandScheduleRequest>();
            foreach (var request in selected)
            {
                string? problem = skipRowChecks ? null
                    : request.IsPendingJob ? "has a pending job"
                    : request.StatusId == StrandRequestStatus.Deleted ? "has a deleted status"
                    : StrandRequestStatus.IsCompleted(request.StatusId) ? "has a completed status"
                    : null;
                if (problem != null)
                    ShowOrdersMessage($"A selected {request.ServiceName} for {request.Address} {problem} and can not be added to My List.", "Add to My List", MessageBoxImage.Warning);
                else
                    rows.Add(request);
            }
            if (rows.Count == 0 || _pickerClosed)
                return;

            // RED's own inspector profile (F-003 O2): an incomplete one refuses before any server call (O9, owner
            // Q-22) and opens Settings, which shows what is missing.
            var profile = dialogProfile;
            if (profile == null)
            {
                _ordersDialogOpen = true; // the guard's message pauses the ORDERS refresh, as ShowOrdersMessage does
                try { profile = ProfileGuard.Require(this, "Add to My List"); }
                finally { _ordersDialogOpen = false; }
                if (profile == null)
                    return;
            }
            string inspectorFolder = profile.Folder;
            if (!Directory.Exists(inspectorFolder))
            {
                ShowOrdersMessage($"Could not find your Inspections folder ({inspectorFolder}). Check it in Settings > Inspector Profile.", "Add to My List", MessageBoxImage.Error);
                return;
            }
            InspectUserKind userKind = profile.Kind;
            string initialsName = profile.InspectorName;

            // Owner 2026-10-20: an order for an earlier day, or more than 30 hours ahead, asks first (once, listing them
            // all); No leaves those out. Orders inside the window go ahead either way.
            var items = OrdersAddRules.ItemsFor(rows, dialogItem);
            var declined = new HashSet<Guid>();
            if (dialogItem == null)
            {
                var now = DateTimeOffset.Now;
                var asked = items.Where(i => OrdersAddRules.NeedsDateQuestion(i, now)).ToList();
                if (asked.Count > 0 && !AskOrdersTouch(OrdersAddRules.DateQuestion(asked, items.Count)))
                {
                    declined.UnionWith(asked.Select(i => i.Id));
                    rows.RemoveAll(r => declined.Contains(r.Id));
                    items.RemoveAll(i => declined.Contains(i.Id));
                }
                if (rows.Count == 0 || _pickerClosed)
                    return;
            }

            // Owner 2026-10-20: other open orders at the same address on the same day can join this add.
            rows.AddRange(ChooseSameAddressOrders(items, rows, declined, inspectorFolder));
            if (_pickerClosed)
                return;

            int toCopy = rows.Count;
            int copied = 0;
            var errors = new List<string>();
            SetOrdersBusy(true, "Adding to My List...");
            try
            {
                // Inspect assigns every valid request to the current user first, in one call.
                bool assigned;
                try
                {
                    await StrandScheduleService.AssignAsync(rows.Select(r => r.Id), StrandScheduleService.Username);
                    assigned = true;
                }
                catch (StrandAuthRequiredException) { throw; }
                catch (Exception ex)
                {
                    errors.Add($"Could not assign the requests to you: {ex.Message}");
                    assigned = false;
                }

                foreach (var row in assigned ? rows : new List<StrandScheduleRequest>())
                {
                    if (_pickerClosed)
                        return;
                    OrdersStatusText.Text = $"Adding {row.JobNumber} to My List...";
                    try
                    {
                        if (await AddOneOrderToMyListAsync(row.Id, inspectorFolder, userKind, initialsName, errors))
                            copied++;
                    }
                    catch (StrandAuthRequiredException) { throw; }
                    catch (Exception ex)
                    {
                        errors.Add($"{row.JobNumber} {row.ServiceName}: {ex.Message}");
                    }
                }
            }
            catch (StrandAuthRequiredException ex)
            {
                errors.Add(ex.Message);
            }
            finally
            {
                SetOrdersBusy(false);
            }
            if (_pickerClosed)
                return;

            if (copied > 0)
                OrdersListView.UnselectAll();
            await LoadOrdersAsync();
            RefreshMyListIfShowing(inspectorFolder);

            if (errors.Count > 0)
                ShowOrdersMessage(string.Join(Environment.NewLine + Environment.NewLine, errors), "Add to My List", MessageBoxImage.Error);
            if (copied != toCopy)
                ShowOrdersMessage($"Only {copied} out of {toCopy} service requests were copied to My List.", "Add to My List", MessageBoxImage.Warning);
            else if (copied == 1)
                ShowOrdersMessage("Copied service request to My List.", "Add to My List", MessageBoxImage.Information);
            else if (copied > 1)
                ShowOrdersMessage($"Copied {copied} service requests to My List.", "Add to My List", MessageBoxImage.Information);
        }

        // One request: re-save it as ours, pick its form, fetch the form from Strand, and save or reuse a copy.
        private async Task<bool> AddOneOrderToMyListAsync(Guid requestId, string inspectorFolder, InspectUserKind userKind, string initialsName, List<string> errors)
        {
            var request = await StrandScheduleService.GetRequestAsync(requestId);
            await StrandScheduleService.UpdateRequestAsync(request);
            request = await StrandScheduleService.GetRequestAsync(requestId);

            if (StrandRequestStatus.IsCompleted(request.StatusId))
            {
                errors.Add($"Cannot add {request.JobNumber} to My List. The service request has already been completed.");
                return false;
            }
            if (request.StatusId == StrandRequestStatus.Deleted)
            {
                errors.Add($"Cannot add {request.JobNumber} to My List. The service request has been cancelled.");
                return false;
            }

            var forms = await StrandScheduleService.GetFormOptionsAsync(request.ServiceId, request.BuilderId);
            int formId = -1;
            if (forms.Count == 1)
            {
                formId = forms[0].Id;
            }
            else if (forms.Count > 1)
            {
                var chooser = new OrderFormChooserWindow(forms, request.ServiceName) { Owner = this };
                _ordersDialogOpen = true;
                try { if (chooser.ShowDialog() == true) formId = chooser.ChosenFormId; }
                finally { _ordersDialogOpen = false; }
            }
            if (formId < 0)
            {
                ShowOrdersMessage($"Could not find a form for {request.ServiceName} service.{Environment.NewLine}" +
                                  $"Please contact Strand Administration to add this service and its associated form to builder {request.BuilderName}.",
                                  "Form Not Found", MessageBoxImage.Warning);
                return false;
            }

            var form = await StrandMyListService.CreateFormAsync(request.IsReinspection, request.JobNumber, formId);
            string inspectionNumber = StrandMyListService.PrepareForm(form, request, initialsName, userKind);

            // Existing copies: same prompts as Inspect. Answering No skips this request (Inspect stops the whole batch).
            var existing = StrandMyListService.FindExistingCopies(inspectorFolder, inspectionNumber);

            // Never delete or move a copy that is open in a RED editor.
            CaptureOpenInspectionLocks();
            if (existing.Any(c => _openFilePaths.Contains(Path.GetFullPath(c.Path)) ||
                                  Application.Current.Windows.OfType<MainWindow>().Any(w => w.HasOpenInspection && PathEquals(w.OpenInspectionFilePath, c.Path))))
            {
                errors.Add($"Cannot add to My List. '{inspectionNumber}' is open in RED. Close it first, then try again.");
                return false;
            }

            // Ask everything first, then act, so a refusal or a Cancel never leaves a deleted My List copy behind.
            var myListCopies = existing.Where(c => c.Folder == InspectFolderKind.MyList).ToList();
            // Like Inspect, an Outbox copy is offered before an Archive copy.
            InspectFolderKind? otherKind = existing.Any(c => c.Folder == InspectFolderKind.Outbox) ? InspectFolderKind.Outbox
                : existing.Any(c => c.Folder == InspectFolderKind.Archive) ? InspectFolderKind.Archive
                : null;
            var otherCopies = otherKind is InspectFolderKind kind ? existing.Where(c => c.Folder == kind).ToList() : new List<ExistingInspectionCopy>();
            string otherFolderName = otherKind == InspectFolderKind.Outbox ? "Outbox" : "Archive";

            // Several copies (e.g. Dropbox conflicted copies): moving them all would keep only the last one.
            if (otherCopies.Count > 1)
            {
                errors.Add($"Cannot add to My List. {otherFolderName} has {otherCopies.Count} files for {inspectionNumber} " +
                           $"({string.Join(", ", otherCopies.Select(c => Path.GetFileName(c.Path)))}). Move the one you want to My List by hand.");
                return false;
            }

            if (myListCopies.Count > 0 &&
                AskOrders($"My List already contains '{inspectionNumber}'. Do you want to replace it?", MessageBoxButton.YesNo) != MessageBoxResult.Yes)
            {
                return false;
            }

            bool moveOtherCopy = false;
            if (otherCopies.Count == 1)
            {
                var answer = AskOrders($"{otherFolderName} already contains '{inspectionNumber}'. Move this copy to My List instead?", MessageBoxButton.YesNoCancel);
                if (answer == MessageBoxResult.No)
                    errors.Add($"Cannot add to My List. {otherFolderName} already contains {inspectionNumber}.");
                if (answer != MessageBoxResult.Yes)
                    return false;
                moveOtherCopy = true;
            }

            // All answers are in. Remove the My List copies being replaced, then move or save.
            foreach (var copy in myListCopies)
                File.Delete(copy.Path);

            if (moveOtherCopy)
            {
                StrandMyListService.MoveToMyList(inspectorFolder, inspectionNumber, otherCopies[0].Path);
                return true;
            }

            StrandMyListService.SaveNewForm(inspectorFolder, inspectionNumber, form);

            // Inspect asks the website for the job's files, for kind Inspector only; a failure here does not undo the copy.
            try { await StrandMyListService.RequestJobFilesAsync(userKind, request.JobNumber); }
            catch (Exception ex) { DiagnosticLogService.Log($"Could not request job files for {request.JobNumber}", ex); }
            return true;
        }

        // The same-address checklist: other open orders in the loaded list (filters ignored) at the address of an order
        // being added, on its day, that are not in this add or declined, and not already in My List. Nothing is ticked.
        private List<StrandScheduleRequest> ChooseSameAddressOrders(List<OrderAddItem> adding, List<StrandScheduleRequest> rows,
                                                                    ISet<Guid> declined, string inspectorFolder)
        {
            var loaded = (OrdersListView.ItemsSource as IEnumerable<StrandScheduleRequest>)?.ToList() ?? new List<StrandScheduleRequest>();
            var exclude = new HashSet<Guid>(rows.Select(r => r.Id));
            exclude.UnionWith(declined);
            var candidates = OrdersAddRules.SameAddressCandidates(adding, loaded, exclude, MyListFileNames(inspectorFolder));
            if (candidates.Count == 0)
                return new List<StrandScheduleRequest>();

            string me = StrandScheduleService.Username;
            var choices = candidates.Select(c => (c, c.ServiceName, OrdersAddRules.CandidateDetail(c, me))).ToList();
            _ordersDialogOpen = true;
            try
            {
                return OrdersTouchDialog.ChooseMany(this, "Add to My List", "Do you want any of these additional orders added to My List as well?",
                                                    choices, "Add selected", "Add none");
            }
            finally { _ordersDialogOpen = false; }
        }

        // The file names (InspectionNumbers) in My List; none when it cannot be read, so every order is offered.
        private static List<string> MyListFileNames(string inspectorFolder)
        {
            try
            {
                return Directory.EnumerateFiles(StrandMyListService.MyListFolder(inspectorFolder), "*.ins", SearchOption.TopDirectoryOnly)
                    .Select(Path.GetFileNameWithoutExtension).OfType<string>().ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                DiagnosticLogService.Log("Could not list My List for the same-address check", ex);
                return new List<string>();
            }
        }

        private bool AskOrdersTouch(string text)
        {
            _ordersDialogOpen = true;
            try { return OrdersTouchDialog.Ask(this, "Add to My List", text); }
            finally { _ordersDialogOpen = false; }
        }

        private MessageBoxResult AskOrders(string text, MessageBoxButton buttons)
        {
            _ordersDialogOpen = true;
            try { return MessageBox.Show(this, text, "Add to My List", buttons, MessageBoxImage.Question); }
            finally { _ordersDialogOpen = false; }
        }

        private void ShowOrdersMessage(string text, string caption, MessageBoxImage icon)
        {
            if (_pickerClosed)
                return;
            _ordersDialogOpen = true;
            try { MessageBox.Show(this, text, caption, MessageBoxButton.OK, icon); }
            finally { _ordersDialogOpen = false; }
        }

        // If the My List tab is showing the Inspect MyList folder, show the new inspection right away.
        private void RefreshMyListIfShowing(string inspectorFolder)
        {
            string myList = StrandMyListService.MyListFolder(inspectorFolder);
            if (!string.IsNullOrWhiteSpace(_currentFolderPath) &&
                string.Equals(Path.GetFullPath(_currentFolderPath).TrimEnd('\\'), Path.GetFullPath(myList).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                RefreshCurrentMyListFolder();
            }
        }
    }
}
