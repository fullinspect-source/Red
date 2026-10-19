using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using InspectionEditor.Models;
using InspectionEditor.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace InspectionEditor
{
    // F-013 Submit from the editor (My List only; Review uses F-018 Approve). The work is Services\SubmitService.cs;
    // this file holds the preconditions (P1-P6), the practice label, the first-live-run question and the result.
    public partial class MainWindow
    {
        private Button? _submitButton;
        private bool _submitRunning;

        private void InstallSubmitButton()
        {
            if (SaveFileButton.Parent is not Panel panel || _submitButton != null)
                return;
            _submitButton = new Button
            {
                Content = "📤 Submit",
                Background = new SolidColorBrush(Color.FromRgb(21, 101, 192)),
                Foreground = Brushes.White,
                BorderThickness = new Thickness(0),
                IsEnabled = false,
                ToolTip = "Finish this report and send it to Strand through the Outbox"
            };
            // F-018 W3: in a file opened from {IF}\Review the same button is Approve.
            _submitButton.Click += async (_, _) =>
            {
                if (CurrentFileCanBeApproved()) await ApproveCurrentReportAsync();
                else await SubmitCurrentReportAsync();
            };
            int index = _completionCheckButton != null ? panel.Children.IndexOf(_completionCheckButton) + 1 : panel.Children.IndexOf(SaveFileButton) + 1;
            panel.Children.Insert(index, _submitButton);
        }

        // P2: only from My List (Outbox and Archive copies cannot be submitted; Review uses Approve, F-018).
        private bool CurrentFileIsInMyList() =>
            !string.IsNullOrEmpty(_currentFilePath) &&
            string.Equals(Path.GetFileName(Path.GetDirectoryName(_currentFilePath)), "MyList", StringComparison.OrdinalIgnoreCase);

        private void UpdateSubmitButton()
        {
            UpdatePrintButton(); // F-022
            if (_submitButton == null)
                return;
            // F-018 V2/W3: a Review file shows Approve instead of Submit.
            bool approve = _currentFilePath != null && CurrentFileCanBeApproved();
            _submitButton.Content = approve ? "✅ Approve" : "📤 Submit";
            // L2: a locked BWT can still be submitted.
            _submitButton.IsEnabled = _currentInspection != null && _saveService.HasFile && (approve || CurrentFileIsInMyList()) && !_submitRunning;
            _submitButton.ToolTip = approve
                ? "Approve this inspection and send it to Strand through the Outbox"
                : CurrentFileIsInMyList()
                    ? "Finish this report and send it to Strand through the Outbox"
                    : "Submit works from My List only";
        }

        // Step 3: RED's Save command, including the V5c sweep that only the Save button ran (A-013-45), then V5d.
        private bool SaveForSubmit()
        {
            if (_currentInspection == null) return false;
            SyncCurrentItemFromUI();
            if (ItemAutoFailRules.ApplySaveCommandSweep(_currentInspection).Count > 0)
                MarkUnsaved();
            return TrySaveCurrentInspection();
        }

        private async Task SubmitCurrentReportAsync()
        {
            if (_submitRunning || _currentInspection == null || _currentFilePath == null || !CurrentFileIsInMyList())
                return;
            if (StrandSyncService.IsRunning)
            {
                MessageBox.Show(this, "Wait for the Sync to finish, then submit.", "Submit", MessageBoxButton.OK, MessageBoxImage.Information);
                return; // P6
            }
            // F-012 L2: a locked BWT is submitted exactly as it is on disk. Any in-memory change would be written by the
            // submit save, so refuse rather than send it.
            if (_readOnlyMode && _hasUnsavedChanges)
            {
                MessageBox.Show(this, "This report is locked, but RED holds unsaved changes for it. Close the report without saving and open it again, then submit.",
                    "Submit", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var profile = ProfileGuard.Require(this, "Submit"); // P4 (F-003 O9)
            if (profile == null)
                return;
            string rawUserType = InspectorProfileService.Load()?.UserType ?? "";
            string src = _currentFilePath;
            string inspections = Path.GetDirectoryName(Path.GetDirectoryName(src)!)!;
            bool practice = !SameFolder(inspections, profile.Folder);

            if (practice && MessageBox.Show(this,
                    $"Practice submit: this report is not in your Inspections folder, so it goes to\n{Path.Combine(inspections, "Outbox")}\n\nContinue?",
                    "Practice submit", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK)
                return;
            // §8 V3a / C9: a practice submit never reaches live Strand. A Preinspector submit is an upload, so it cannot
            // be practised from a folder outside the Inspections folder.
            if (practice && profile.Kind == InspectUserKind.Preinspector)
            {
                MessageBox.Show(this, "A practice submit never uploads to Strand, and a Preinspector submit is an upload. Submit this report from your Inspections folder's My List instead.",
                    "Practice submit", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // P1 kind Other: INSPECT submits it like Inspector; RED says so once per value.
            if (profile.Kind == InspectUserKind.Other && !SubmitState.HasSeenOtherNotice(rawUserType))
            {
                MessageBox.Show(this, SubmitService.OtherKindNotice(rawUserType), "Submit", MessageBoxButton.OK, MessageBoxImage.Information);
                SubmitState.MarkOtherNoticeSeen(rawUserType);
            }

            // §8: the first live Submit of each kind on this device needs the owner present.
            string flagKind = profile.Kind == InspectUserKind.Other ? InspectUserKind.Inspector.ToString() : profile.Kind.ToString();
            bool firstLive = !practice && !SubmitState.IsLiveVerified(flagKind);
            if (firstLive && MessageBox.Show(this,
                    $"This is the first live Submit for a {flagKind} user on this device. Is the owner present?",
                    "First live Submit", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            var removedWorkingCopies = new List<string>();
            var ctx = new SubmitContext
            {
                Model = _currentInspection,
                Saver = _saveService,
                SourcePath = src,
                Profile = profile,
                RawUserType = rawUserType,
                SaveWithSweeps = () => _readOnlyMode || SaveForSubmit(), // L2: a locked BWT has nothing to save
                Log = InspectFormatSubmitLog.Instance, // F-019 L7
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
                    .ToList(),
                DeleteRemovedWorkingCopies = names => removedWorkingCopies.AddRange(names),
                Website = profile.Kind == InspectUserKind.Preinspector ? new StrandWebsiteUploader(StrandWebsiteSession.Default) : null
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
                DiagnosticLogService.Log("Submit failed unexpectedly", ex);
                result = new SubmitResult { Stage = SubmitStage.Refused, Message = "Cannot submit inspection. " + ex.Message };
            }
            finally
            {
                Cursor = System.Windows.Input.Cursors.Arrow;
                _submitRunning = false;
                UpdateSubmitButton();
            }

            switch (result.Stage)
            {
                case SubmitStage.Invalid:
                    MessageBox.Show(this, result.Message, "Submit", MessageBoxButton.OK, MessageBoxImage.Warning);
                    if (result.Problems != null) ShowProblemList(result.Problems);
                    return;
                case SubmitStage.OpenAttachment:
                    MessageBox.Show(this, result.Message, "Submit", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                case SubmitStage.Done:
                case SubmitStage.SourceNotRemoved:
                    if (firstLive && MessageBox.Show(this, $"Owner: mark the first live {flagKind} Submit on this device as checked (Outbox picked up, Archive processed)?\n\nNo asks again next time.",
                            "First live Submit", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                        SubmitState.MarkLiveVerified(flagKind);
                    // F-014 S1: remember what RED submitted, for the waiting / processed / not-returned notes.
                    if (result.OutboxPath != null)
                        SubmittedTracker.Record(Path.GetFileNameWithoutExtension(result.OutboxPath), inspections, profile.Kind, DateTime.Now);
                    foreach (var entry in _pdfSessions.Where(e => ReferenceEquals(e.Owner, _currentInspection)).ToArray())
                    {
                        try { entry.Monitor.Cleanup(); _pdfSessions.Remove(entry); } catch { }
                    }
                    _hasUnsavedChanges = false;
                    CloseReportWithoutSaving();
                    MessageBox.Show(this, result.Message, "Submit", MessageBoxButton.OK,
                        result.Stage == SubmitStage.Done ? MessageBoxImage.Information : MessageBoxImage.Warning);
                    if (_openedAsCompanion) Close(); // back to My List
                    return;
                default:
                    MessageBox.Show(this, result.Message, "Submit", MessageBoxButton.OK, MessageBoxImage.Warning);
                    if (result.SourceRewritten && File.Exists(src))
                    {
                        // {SRC} now holds the final bytes: reload so later edits start from what is on disk.
                        _hasUnsavedChanges = false;
                        _diskConflictPending = false;
                        LoadInspectionFileAsync(src);
                    }
                    return;
            }
        }

        private string AttachmentName(int index)
        {
            var attachment = _currentInspection?.Attachments is { } list && index >= 0 && index < list.Count ? list[index] : null;
            var json = attachment as JObject ?? (attachment == null ? null : JObject.FromObject(attachment));
            return (string?)json?["Filename"] ?? "";
        }

        private static bool SameFolder(string a, string b)
        {
            try
            {
                return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }

    // RED's own Submit state on this device (never INSPECT's config): first-live flags per kind and the kind-Other notice.
    internal static class SubmitState
    {
        private sealed class State
        {
            public HashSet<string> LiveVerifiedKinds { get; set; } = new(StringComparer.Ordinal);
            public HashSet<string> OtherNoticesSeen { get; set; } = new(StringComparer.Ordinal);
        }

        private static string StatePath => Path.Combine(AppIdentity.LocalAppDataPath, "submit-state.json");

        private static State Load()
        {
            try { return File.Exists(StatePath) ? JsonConvert.DeserializeObject<State>(File.ReadAllText(StatePath)) ?? new State() : new State(); }
            catch { return new State(); }
        }

        private static void Save(State state)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            string temp = StatePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, JsonConvert.SerializeObject(state, Formatting.Indented));
            File.Move(temp, StatePath, overwrite: true);
        }

        public static bool IsLiveVerified(string kind) => Load().LiveVerifiedKinds.Contains(kind);
        public static void MarkLiveVerified(string kind) { var s = Load(); s.LiveVerifiedKinds.Add(kind); Save(s); }
        public static bool HasSeenOtherNotice(string value) => Load().OtherNoticesSeen.Contains(value);
        public static void MarkOtherNoticeSeen(string value) { var s = Load(); s.OtherNoticesSeen.Add(value); Save(s); }
    }
}
