using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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
    // F-013 Submit from the editor (My List only; a Review file shows no Submit, F-018 owner 2026-10-20: no Approve). The
    // work is Services\SubmitService.cs; this file holds the preconditions (P1-P6), the practice label, the first-live-run
    // question and the result.
    public partial class MainWindow
    {
        private Button? _submitButton;
        private TextBlock? _submitHint;
        private bool _submitRunning;

        // Owner tablet test 2026-10-20 ("could not find how to submit"): Submit sits right after Save, larger, and a hint
        // beside it says why it cannot run (Services\SubmitAvailability.cs). No popup.
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
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                IsEnabled = false,
                ToolTip = "Finish this report and send it to Strand through the Outbox"
            };
            ToolTipService.SetShowOnDisabled(_submitButton, true);
            System.Windows.Automation.AutomationProperties.SetAutomationId(_submitButton, "SubmitButton");
            _submitButton.Click += async (_, _) => await SubmitCurrentReportAsync();
            _submitHint = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromRgb(254, 226, 226)),
                FontStyle = FontStyles.Italic,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 8, 0),
                Visibility = Visibility.Collapsed
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(_submitHint, "SubmitHint");
            int index = panel.Children.IndexOf(SaveFileButton) + 1;
            panel.Children.Insert(index, _submitButton);
            panel.Children.Insert(index + 1, _submitHint);
            InstallToolbarWrap();
        }

        // P2: only from My List (Outbox and Archive copies cannot be submitted; Review files never, F-018).
        private bool CurrentFileIsInMyList() => SubmitAvailability.IsInFolder(_currentFilePath, "MyList");

        private void UpdateSubmitButton()
        {
            UpdatePrintButton(); // F-022
            if (_submitButton == null)
                return;
            // L2: a locked BWT can still be submitted.
            var state = SubmitAvailability.For(_currentInspection != null, _saveService.HasFile, _currentFilePath, _submitRunning);
            _submitButton.Visibility = state.Visible ? Visibility.Visible : Visibility.Collapsed;
            _submitButton.IsEnabled = state.Enabled;
            _submitButton.ToolTip = state.Enabled || state.Hint.Length == 0 ? "Finish this report and send it to Strand through the Outbox" : state.Hint;
            if (_submitHint != null)
            {
                _submitHint.Text = state.Hint;
                _submitHint.Visibility = state.Visible && state.Hint.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        // The toolbar starts and wraps inside the part of this window that is on screen (a window wider than its screen, as
        // RED's 1100 px minimum on a tablet in portrait, keeps every button on screen), leaving room for the Help button
        // when the window's right edge is on screen.
        private void InstallToolbarWrap()
        {
            TopBarGrid.SizeChanged += (_, _) => UpdateToolbarWrap();
            LocationChanged += (_, _) => UpdateToolbarWrap();
            Loaded += (_, _) => UpdateToolbarWrap();
        }

        // Measured on the window's client area: the top bar itself reports its full content width once the buttons overflow.
        private void UpdateToolbarWrap()
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero || PresentationSource.FromVisual(TopBarGrid) == null)
                return;
            try
            {
                double scale = VisualTreeHelper.GetDpi(TopBarGrid).DpiScaleX;
                var client = new NativePoint();
                if (scale <= 0 || !GetClientRect(hwnd, out var rect) || !ClientToScreen(hwnd, ref client))
                    return;
                var origin = TopBarGrid.PointToScreen(new Point(0, 0)); // device pixels
                double barRight = client.X + rect.Right - 8 * scale;    // the top bar's 8 px padding
                var area = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)Math.Max(origin.X, 0), (int)origin.Y)).WorkingArea;
                double left = Math.Max(origin.X, area.Left), right = Math.Min(barRight, area.Right);
                double help = barRight <= area.Right ? 60 : 8; // Help button: 36 px wide, 12 px from the window's right edge
                double leftCut = (left - origin.X) / scale;
                EditorToolbar.Margin = new Thickness(leftCut, 0, 0, 0);
                EditorToolbar.MaxWidth = Math.Max(240, (right - left) / scale - help);
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is ArgumentException) { }
        }

        [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out NativeRect rect);
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint point);

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

            // Owner 2026-10-21 (tablet test of 2.3.5): no "first live Submit / is the owner present?" questions; Submit
            // asks nothing INSPECT does not ask.
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
                    // F-014 S1: remember what RED submitted, for the waiting / processed / not-returned notes.
                    if (result.OutboxPath != null)
                        SubmittedTracker.Record(Path.GetFileNameWithoutExtension(result.OutboxPath), inspections, profile.Kind, DateTime.Now);
                    App.SweepMyListBackupsSoon(); // storage audit F2: old backups of other reports gone from My List
                    foreach (var entry in _pdfSessions.Where(e => ReferenceEquals(e.Owner, _currentInspection)).ToArray())
                    {
                        try { entry.Monitor.Cleanup(); _pdfSessions.Remove(entry); } catch { }
                    }
                    _hasUnsavedChanges = false;
                    CloseReportWithoutSaving();
                    // Owner 2026-10-21 (tablet test of 2.3.5): a successful Submit just closes the report, with no "Sent to
                    // Outbox" box. A NonDropBox user still gets the one line that says to press Sync; a problem still shows.
                    if (result.Stage != SubmitStage.Done || profile.Kind == InspectUserKind.NonDropBox)
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

    // RED's own Submit state on this device (never INSPECT's config): the kind-Other notice. (The first-live flags are
    // gone with their questions, owner 2026-10-21; an old file's LiveVerifiedKinds is ignored.)
    internal static class SubmitState
    {
        private sealed class State
        {
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

        public static bool HasSeenOtherNotice(string value) => Load().OtherNoticesSeen.Contains(value);
        public static void MarkOtherNoticeSeen(string value) { var s = Load(); s.OtherNoticesSeen.Add(value); Save(s); }
    }
}
