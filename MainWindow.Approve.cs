using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using InspectionEditor.Services;

namespace InspectionEditor
{
    // F-018 Approve from the editor (a file opened from {IF}\Review, W3). The toolbar Submit button becomes Approve and
    // runs SPEC A1: Services\SubmitService.cs in Approve mode. The first live approval is owner-supervised (SPEC §7 V3).
    public partial class MainWindow
    {
        private bool CurrentFileCanBeApproved() =>
            ReviewAccess.CanApprove(_currentFilePath, InspectorProfileService.Load()?.InspectionsFolder);

        private async Task ApproveCurrentReportAsync()
        {
            if (_submitRunning || _currentInspection == null || _currentFilePath == null)
                return;
            if (!CurrentFileCanBeApproved())
            {
                MessageBox.Show(this, ReviewAccess.ApprovePermissionRefused, "Approve", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (StrandSyncService.IsRunning)
            {
                MessageBox.Show(this, "Wait for the Sync to finish, then approve.", "Approve", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            // F-012 L2 (as Submit): a locked BWT is approved exactly as it is on disk; in-memory changes would be written.
            if (_readOnlyMode && _hasUnsavedChanges)
            {
                MessageBox.Show(this, "This report is locked, but RED holds unsaved changes for it. Close the report without saving and open it again, then approve.",
                    "Approve", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var profile = ProfileGuard.Require(this, "Approve"); // A1 step 1: F-013 P4
            if (profile == null)
                return;
            string src = _currentFilePath;
            if (!ReviewAccess.TryBegin(src)) // A3: one run per file
                return;
            if (!SubmitState.IsLiveVerified("Approve") && MessageBox.Show(this,
                    "This is the first live Approve on this device. The back office has never received an approval flag (F-018 §7 V3). Is the owner present?",
                    "First live Approve", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            {
                ReviewAccess.End(src);
                return;
            }

            var ctx = new SubmitContext
            {
                Approve = true,
                Model = _currentInspection,
                Saver = _saveService,
                SourcePath = src,
                Profile = profile,
                RawUserType = InspectorProfileService.Load()?.UserType,
                SaveWithSweeps = () => _readOnlyMode || SaveForSubmit(), // A1 step 3: pending edits, without the stamp; a locked BWT has none
                Log = InspectFormatSubmitLog.Instance,
                Validate = RunCompletionCheck,
                HasOpenAttachment = () =>
                {
                    PollPdfMonitors();
                    return _pdfSessions.Any(entry => ReferenceEquals(entry.Owner, _currentInspection) && !entry.Monitor.CanLeave(out _));
                },
                RefreshAttachments = PollPdfMonitors,
                WorkingCopies = () => _pdfSessions
                    .Where(entry => ReferenceEquals(entry.Owner, _currentInspection) && entry.Monitor.AttachmentIndex is int)
                    .Select(entry => (AttachmentName(entry.Monitor.AttachmentIndex!.Value), (string?)entry.Monitor.WorkingPath))
                    .ToList()
            };

            _submitRunning = true;
            UpdateSubmitButton();
            SubmitResult result;
            try
            {
                Cursor = System.Windows.Input.Cursors.Wait;
                result = await SubmitService.SubmitAsync(ctx);
            }
            catch (Exception ex)
            {
                DiagnosticLogService.Log("Approve failed unexpectedly", ex);
                result = new SubmitResult { Stage = SubmitStage.Refused, Message = "Cannot approve inspection. " + ex.Message };
            }
            finally
            {
                Cursor = System.Windows.Input.Cursors.Arrow;
                _submitRunning = false;
                ReviewAccess.End(src);
                UpdateSubmitButton();
            }

            switch (result.Stage)
            {
                case SubmitStage.Invalid:
                    MessageBox.Show(this, result.Message, "Approve", MessageBoxButton.OK, MessageBoxImage.Warning);
                    if (result.Problems != null) ShowProblemList(result.Problems);
                    return;
                case SubmitStage.OpenAttachment:
                    MessageBox.Show(this, result.Message, "Approve", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                case SubmitStage.Done:
                case SubmitStage.SourceNotRemoved:
                    if (!SubmitState.IsLiveVerified("Approve") && MessageBox.Show(this, "Owner: mark the first live Approve on this device as checked (processed by Strand as expected)?\n\nNo asks again next time.",
                            "First live Approve", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                        SubmitState.MarkLiveVerified("Approve");
                    foreach (var entry in _pdfSessions.Where(e => ReferenceEquals(e.Owner, _currentInspection)).ToArray())
                    {
                        try { entry.Monitor.Cleanup(); _pdfSessions.Remove(entry); } catch { }
                    }
                    _hasUnsavedChanges = false;
                    CloseReportWithoutSaving();
                    MessageBox.Show(this, result.Message, "Approve", MessageBoxButton.OK,
                        result.Stage == SubmitStage.Done ? MessageBoxImage.Information : MessageBoxImage.Warning);
                    if (_openedAsCompanion) Close(); // A1 step 8: back to the Review list
                    return;
                default:
                    MessageBox.Show(this, result.Message, "Approve", MessageBoxButton.OK, MessageBoxImage.Warning);
                    if (result.SourceRewritten && File.Exists(src))
                    {
                        _hasUnsavedChanges = false;
                        _diskConflictPending = false;
                        LoadInspectionFileAsync(src);
                    }
                    return;
            }
        }
    }
}
