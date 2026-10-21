using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using InspectionEditor.Services;

namespace InspectionEditor
{
    // Edit Service Request / Complete Survey Request, as opened from the ORDERS grid.
    public partial class OrderRequestWindow : Window
    {
        private readonly Guid _requestId;
        private StrandRequestDetail? _request;
        // True only while a write (save, delete, complete, job number) is in flight.
        private bool _saving;
        private readonly System.Threading.CancellationTokenSource _loadCts = new();

        // True once anything was written to Strand, so the grid reloads.
        public bool ChangesSaved { get; private set; }

        // Set when the user chose Add to My List; the ORDERS tab then copies this request to My List.
        public bool AddToMyListRequested { get; private set; }

        // Set when Add to My List was refused for an incomplete inspector profile; the ORDERS tab opens Settings
        // once this dialog closes. The dialog stays open so unsaved edits can still be saved.
        public bool OpenSettingsRequested { get; private set; }

        // The inspector profile checked before the assign PATCH, handed on with AddToMyListRequested.
        public CompleteProfile? CheckedProfile { get; private set; }

        // The request as saved for Add to My List (its edited date answered the date question), handed on with
        // AddToMyListRequested for the same-address check.
        public OrderAddItem? AddedItem { get; private set; }

        public OrderRequestWindow(Guid requestId)
        {
            _requestId = requestId;
            InitializeComponent();
            Loaded += async (_, _) => await LoadAsync();
        }

        private async Task LoadAsync()
        {
            SetBusy(true);
            try
            {
                var requestTask = StrandScheduleService.GetRequestAsync(_requestId, _loadCts.Token);
                var inspectorsTask = StrandScheduleService.GetInspectorsAsync(_loadCts.Token);
                _request = await requestTask;
                List<StrandUserOption> inspectors;
                try { inspectors = await inspectorsTask; }
                catch (Exception) { inspectors = StrandScheduleService.ArrangeInspectorOptions(Array.Empty<StrandUserOption>()); }
                if (_loadCts.IsCancellationRequested)
                    return;
                Bind(_request, inspectors);
                SetBusy(false);
            }
            catch (Exception ex)
            {
                if (_loadCts.IsCancellationRequested)
                    return; // the user closed the window while it was loading
                SetBusy(false);
                string caption = ex is StrandAuthRequiredException ? "Sign in to Strand" : "Could not open service request";
                MessageBox.Show(this, ex.Message, caption, MessageBoxButton.OK, MessageBoxImage.Warning);
                Close();
            }
        }

        // A write in flight must finish before the window closes, otherwise a successful
        // change would be reported as a failure. Loading can always be abandoned.
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (_saving)
                e.Cancel = true;
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            _loadCts.Cancel();
            base.OnClosed(e);
        }

        private void CloseWithSavedChanges()
        {
            ChangesSaved = true;
            SetBusy(false);
            DialogResult = true;
        }

        private void Bind(StrandRequestDetail request, List<StrandUserOption> inspectors)
        {
            bool isSurvey = request.StatusId == StrandRequestStatus.FieldComplete;
            Title = isSurvey ? "Complete Survey Request" : "Edit Service Request";
            AddressText.Text = string.Join(", ", new[] { request.Address, request.City }.Where(s => !string.IsNullOrWhiteSpace(s)));
            SubtitleText.Text = $"Job: {Fallback(request.JobNumber)}    Service: {Fallback(request.ServiceName)}";
            PendingJobBanner.Visibility = request.IsPendingJob ? Visibility.Visible : Visibility.Collapsed;

            BuilderText.Text = request.BuilderName;
            StatusText.Text = request.StatusName;
            SubdivisionText.Text = request.SubdivisionName;
            LotBlockText.Text = $"{Fallback(request.Lot)} / {Fallback(request.Block)}";
            RequestorText.Text = JoinNonEmpty(request.RequestorName, FormatPhone(request.RequestorPhone));
            SuperintendentText.Text = JoinNonEmpty(request.SuperintendentName, FormatPhone(request.SuperintendentPhone));
            RequestedForText.Text = FormatDate(request.RequestedForDate, request.RequestTimeRequired);
            AssignedOnText.Text = FormatDate(request.AssignedDate, true);
            CompletedOnText.Text = FormatDate(request.CompletedDate, true);

            if (!string.IsNullOrWhiteSpace(request.AssignedUsername) &&
                !inspectors.Any(i => string.Equals(i.Username, request.AssignedUsername, StringComparison.OrdinalIgnoreCase)))
            {
                inspectors.Add(new StrandUserOption { Username = request.AssignedUsername, DisplayName = Fallback(request.AssignedName, request.AssignedUsername) });
            }
            AssignedCombo.ItemsSource = inspectors;
            // An unassigned request shows the "[Unassigned]" row, as INSPECT does (its username may not be blank).
            AssignedCombo.SelectedItem = string.IsNullOrWhiteSpace(request.AssignedUsername)
                ? inspectors.FirstOrDefault(i => i.IsUnassigned) ?? inspectors.FirstOrDefault()
                : inspectors.FirstOrDefault(i => string.Equals(i.Username, request.AssignedUsername, StringComparison.OrdinalIgnoreCase))
                  ?? inspectors.FirstOrDefault();

            DateTime? local = request.Date?.ToLocalTime().DateTime;
            ScheduledDatePicker.SelectedDate = local?.Date;
            ScheduledTimeBox.Text = local.HasValue && (request.RequestTimeRequired || local.Value.TimeOfDay != TimeSpan.Zero)
                ? local.Value.ToString("h:mm tt", CultureInfo.CurrentCulture)
                : "";
            ReinspectionCheckBox.IsChecked = request.IsReinspection;
            AttemptBox.Text = request.Attempt.ToString(CultureInfo.CurrentCulture);
            PurchaseOrderBox.Text = request.PurchaseOrderNumber;
            RequisitionBox.Text = request.RequisitionNumber;
            SpecialRequestBox.Text = request.SpecialRequest;
            HistoryBox.Text = string.Join(Environment.NewLine, request.History ?? new List<string>());

            CompletedButton.Visibility = isSurvey ? Visibility.Visible : Visibility.Collapsed;
            FailedButton.Visibility = isSurvey ? Visibility.Visible : Visibility.Collapsed;
            bool closed = request.StatusId == StrandRequestStatus.Deleted || StrandRequestStatus.IsCompleted(request.StatusId);
            SaveButton.IsEnabled = !closed;
            DeleteButton.IsEnabled = request.StatusId != StrandRequestStatus.Deleted;
            // Inspect hides Delete without the Strand "Service Request Delete" or "Edit - All" permission.
            DeleteButton.Visibility = StrandScheduleService.CanDeleteRequests ? Visibility.Visible : Visibility.Collapsed;

            // Owner 2026-10-20: Assign is gone, so Assigned To is shown and never changed here, for everyone. Save sends
            // it back as shown; Add to My List assigns the request to me.
            AssignedCombo.IsEnabled = false;
            // Inspect offers Add to My List for open requests (Requested or Assigned) without a pending job.
            AddToMyListButton.Visibility = (request.StatusId is StrandRequestStatus.Requested or StrandRequestStatus.Assigned) && !request.IsPendingJob
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private async void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (_request == null || !TryReadForm(out var updated))
                return;

            SetBusy(true, saving: true);
            try
            {
                await StrandScheduleService.UpdateRequestAsync(updated);
                CloseWithSavedChanges();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Service request failed to update. Please try again.\n\n{ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                SetBusy(false);
            }
        }

        // Like Inspect's dialog: save the edits with the request assigned to me, then hand it to My List.
        private async void AddToMyListButton_Click(object sender, RoutedEventArgs e)
        {
            if (_request == null)
                return;
            // Check RED can save a form before assigning anything on the server. An incomplete inspector profile
            // refuses before the assign PATCH; Settings opens when this dialog closes (Save or Cancel), so edits
            // made here are not lost.
            var profile = ProfileGuard.Require(this, Title);
            if (profile == null)
            {
                OpenSettingsRequested = true; // the picker opens no further order dialogs
                return;
            }
            if (!System.IO.Directory.Exists(profile.Folder))
            {
                MessageBox.Show(this, $"Could not find your Inspections folder ({profile.Folder}). Check it in Settings > Inspector Profile.",
                    Title, MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            OpenSettingsRequested = false;
            if (!TryReadForm(out var updated))
                return;

            // Owner 2026-10-20: a request for an earlier day, or more than 30 hours ahead, asks first, with the date as
            // set here. No sends nothing and keeps the dialog open.
            var item = new OrderAddItem(updated.Id, updated.ServiceName, updated.Address, updated.City, updated.Date, updated.RequestTimeRequired, updated.ServiceTypeId);
            if (OrdersAddRules.NeedsDateQuestion(item, DateTimeOffset.Now) &&
                !OrdersTouchDialog.Ask(this, Title, OrdersAddRules.DateQuestion(new[] { item }, 1)))
                return;

            updated.AssignedUsername = StrandScheduleService.Username;
            SetBusy(true, saving: true);
            try
            {
                await StrandScheduleService.UpdateRequestAsync(updated);
                CheckedProfile = profile;
                AddedItem = item;
                AddToMyListRequested = true;
                CloseWithSavedChanges();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Cannot assign request.\n\n{ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private bool TryReadForm(out StrandRequestDetail updated)
        {
            updated = _request!;
            if (ScheduledDatePicker.SelectedDate is not DateTime date)
            {
                MessageBox.Show(this, "Please set the scheduled date for the service request.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }

            TimeSpan time = TimeSpan.Zero;
            string timeText = ScheduledTimeBox.Text.Trim();
            if (timeText.Length > 0)
            {
                if (!DateTime.TryParse(timeText, CultureInfo.CurrentCulture, DateTimeStyles.NoCurrentDateDefault, out var parsedTime))
                {
                    MessageBox.Show(this, "Enter the scheduled time like 9:30 AM.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
                    return false;
                }
                time = parsedTime.TimeOfDay;
            }
            else if (_request!.RequestTimeRequired)
            {
                MessageBox.Show(this, "This service requires a scheduled time.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }

            if (!int.TryParse(AttemptBox.Text.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out int attempt) || attempt < 0)
            {
                MessageBox.Show(this, "Attempt must be a whole number.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }

            string purchaseOrder = PurchaseOrderBox.Text.Trim();
            string requisition = RequisitionBox.Text.Trim();
            if (_request!.PurchaseOrderRequired && purchaseOrder.Length == 0 && requisition.Length == 0)
            {
                MessageBox.Show(this, "Please provide a purchase order or requisition number for the service request.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }

            var localDateTime = DateTime.SpecifyKind(date.Date + time, DateTimeKind.Local);
            _request.Date = new DateTimeOffset(localDateTime);
            _request.AssignedUsername = (AssignedCombo.SelectedItem as StrandUserOption)?.Username ?? "";
            _request.IsReinspection = ReinspectionCheckBox.IsChecked == true;
            _request.Attempt = attempt;
            _request.PurchaseOrderNumber = purchaseOrder;
            _request.RequisitionNumber = requisition;
            updated = _request;
            return true;
        }

        private async void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            if (_request == null)
                return;

            var reasonWindow = new OrderDeleteReasonWindow(1) { Owner = this };
            if (reasonWindow.ShowDialog() != true)
                return;

            SetBusy(true, saving: true);
            try
            {
                await StrandScheduleService.DeleteAsync(_request.Id, reasonWindow.ReasonText);
                CloseWithSavedChanges();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Unable to delete {Fallback(_request.ServiceName, "request")}. Please try again.\n\n{ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void SetJobNumberButton_Click(object sender, RoutedEventArgs e)
        {
            if (_request == null)
                return;

            var input = new InputDialog("Enter the job number for this request:", "Set Job Number") { Owner = this };
            if (input.ShowDialog() != true || string.IsNullOrWhiteSpace(input.ResponseText))
                return;

            SetBusy(true, saving: true);
            try
            {
                await StrandScheduleService.UpdateJobNumberAsync(_request.Id, input.ResponseText.Trim());
                _saving = false; // written; the reload below may be abandoned
                ChangesSaved = true;
                MessageBox.Show(this, $"Successfully updated job number for {Fallback(_request.ServiceName, "request")}.", Title, MessageBoxButton.OK, MessageBoxImage.Information);
                await LoadAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Unable to update job number for {Fallback(_request.ServiceName, "request")}.\n\n{ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void CompletedButton_Click(object sender, RoutedEventArgs e) => await CompleteSurveyAsync(passed: true);

        private async void FailedButton_Click(object sender, RoutedEventArgs e) => await CompleteSurveyAsync(passed: false);

        private async Task CompleteSurveyAsync(bool passed)
        {
            if (_request == null)
                return;

            SetBusy(true, saving: true);
            try
            {
                await StrandScheduleService.CompleteSurveyAsync(_request.Id, passed);
                CloseWithSavedChanges();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Could not complete request. Please try again.\n\n{ex.Message}", Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void SetBusy(bool busy, bool saving = false)
        {
            _saving = busy && saving;
            Cursor = busy ? System.Windows.Input.Cursors.Wait : null;
            IsEnabled = !busy;
        }

        private static string FormatDate(DateTimeOffset? value, bool includeTime) =>
            value is not DateTimeOffset date
                ? ""
                : date.ToLocalTime().DateTime.ToString(includeTime ? "M/d/yyyy h:mm tt" : "M/d/yyyy", CultureInfo.CurrentCulture);

        private static string FormatPhone(string? phone)
        {
            string digits = new string((phone ?? "").Where(char.IsDigit).ToArray());
            return digits.Length == 10 ? $"({digits[..3]}) {digits.Substring(3, 3)}-{digits[6..]}" : phone ?? "";
        }

        private static string JoinNonEmpty(params string?[] parts) =>
            string.Join("  ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

        private static string Fallback(string? value, string fallback = "—") =>
            string.IsNullOrWhiteSpace(value) ? fallback : value;
    }
}
