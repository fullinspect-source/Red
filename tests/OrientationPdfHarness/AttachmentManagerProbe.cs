using InspectionEditor.Models;
using InspectionEditor.Services;
using Newtonsoft.Json.Linq;

namespace System.Windows
{
    public class RoutedEventArgs : EventArgs { }
    public enum MessageBoxButton { OK }
    public enum MessageBoxImage { Warning }
    public static class MessageBox
    {
        public static void Show(string text, string title, MessageBoxButton button, MessageBoxImage image) { }
    }
}
namespace System.Windows.Threading
{
    public sealed class DispatcherTimer
    {
        public TimeSpan Interval { get; set; }
        public event EventHandler? Tick;
        public void Start() { }
        public void Stop() { }
    }
}
namespace InspectionEditor
{
    static class DiagnosticLogService { public static void Log(string message, Exception ex) { } }
    public sealed class AttachmentsWindow
    {
        public sealed class PdfRow
        {
            public int Index { get; init; }
            public string Filename { get; init; } = "";
            public string Size { get; init; } = "";
            public string Status { get; init; } = "";
            public bool CanOpen { get; init; } = true;
        }
        public static Action<AttachmentsWindow>? OnShow;
        public object? Owner { get; set; }
        public readonly Func<List<PdfRow>> Rows;
        public readonly Action<int> Open;
        public readonly Func<string, Func<long, bool>, bool> Add;
        public readonly Func<IReadOnlyCollection<int>, bool> Delete;
        public readonly Action Retry;
        public AttachmentsWindow(bool readOnly, Func<List<PdfRow>> rows, Action<int> open,
            Func<string, Func<long, bool>, bool> add, Func<IReadOnlyCollection<int>, bool> delete, Action retry)
        { Rows = rows; Open = open; Add = add; Delete = delete; Retry = retry; }
        public static string ReadableSize(long bytes) => bytes.ToString();
        public void Activate() { }
        public void RefreshRows() { }
        public void ShowDialog() => OnShow!(this);
    }
    public partial class MainWindow
    {
        private InspectionFile? _currentInspection;
        private string? _currentFilePath;
        private bool _readOnlyMode, _isLoadingFile, _savingEditorChanges, _finishingOrientationPdf;
        private PdfEditMonitor? _orientationPdf;
        private string ProbeRoot = "";
        private bool SaveFails;
        private int SaveCount;
        private void SyncCurrentItemFromUI() { }
        private void MarkUnsaved() { }
        private void UpdateOrientationPdfControls() { }
        private bool TrySaveCurrentInspection() { SaveCount++; return !SaveFails; }

        public static void RunAttachmentManagerProbe(string root, Action<bool,string> check)
        {
            var p = new MainWindow { ProbeRoot = root, _currentFilePath = Path.Combine(root,"manager.ins"),
                _currentInspection = JObject.Parse("""
                {"InspectionCode":"BWT","Contact":"Manager buyer","Sections":[{"Items":[{"ItemId":1,"Name":"Walk","ControlName":"DocumentButton","Template":"walk.pdf"}]}],"Attachments":[]}
                """).ToObject<InspectionFile>()! };
            p._currentInspection.Attachments!.Add(PdfAttachmentService.Create("walk.pdf", AutofillProbe.SyntheticPdf("")));
            p._currentInspection.Attachments.Add(PdfAttachmentService.Create("other.pdf", AutofillProbe.SyntheticPdf("other")));
            AttachmentsWindow.OnShow = window =>
            {
                window.Open(0); window.Open(1);
                var officialToken = (JObject)p._currentInspection.Attachments![0];
                var data = officialToken["FileData"]!.DeepClone();
                officialToken["FileData"] = "corrupt!";
                var damaged = window.Rows().Single(r => r.Index == 0);
                check(!damaged.CanOpen && damaged.Status.Contains("Damaged") && damaged.Size.Contains("Unknown"),
                    "production manager damaged status wins over existing healthy monitor and size is unknown");
                bool blocked = false;
                try { window.Open(0); } catch (IOException) { blocked = true; }
                check(blocked && p._pdfSessions.Count == 2 && p.SaveCount == 0,
                    "production manager cannot reopen healthy stale copy over newly corrupt embedded row");
                try { OrientationPdfSession.PreferredCandidateIndex(p._currentInspection, OrientationPdfSession.FindCandidates(p._currentInspection)); blocked = false; }
                catch (IOException ex) { blocked = ex.Message.Contains("ATTACHMENTS") && ex.Message.Contains("repair"); }
                check(blocked, "production walk selection rejects corrupt official even when prepared monitor exists");
                officialToken["FileData"] = data;
                check(window.Rows().Single(r => r.Index == 0).CanOpen,
                    "production manager refresh recovers open eligibility only when embedded data repaired");
                var selected = p.FindPdfMonitor(0)!; var other = p.FindPdfMonitor(1)!;
                p._orientationPdf = selected;
                check(AutofillProbe.Values(File.ReadAllBytes(selected.WorkingPath))["Customer Name_HOI"] == "Manager buyer",
                    "production manager callback uses official walk blank autofill");
                p.SaveFails = true;
                check(!window.Delete(new[] {0}) && p._pdfSessions.Count == 2 && ReferenceEquals(p._orientationPdf, selected) && File.Exists(selected.WorkingPath),
                    "production failed delete retains registered sessions and walk pointer");
                p.SaveFails = false;
                check(window.Delete(new[] {0}) && p._pdfSessions.Count == 1 && p._orientationPdf == null && ReferenceEquals(p.FindPdfMonitor(0), other),
                    "production successful delete unregisters only selected and reuses rebased monitor");
                string source = Path.Combine(root,"manager-add.pdf");
                File.WriteAllBytes(source, AutofillProbe.SyntheticPdf("added"));
                p.SaveFails = true;
                check(!window.Add(source, _ => true) && p._pdfSessions.Count == 2,
                    "production failed add remains registered for retry and navigation guards");
                var row = window.Rows().Single(r => r.Index < 0);
                var pending = p._pdfSessions.Single(e => e.Monitor.IsPendingAdd).Monitor;
                check(row.Status.Contains("Retry Save") && row.Filename == "manager-add.pdf" && !pending.CanLeave(out _),
                    "production failed add appears in manager with retry status and blocks leave");
                check(!p.FinishOrientationEditing(leaving: false) && p._pdfSessions.Count == 2 && File.Exists(pending.WorkingPath),
                    "production leave guard blocks failed add and retains all registered sessions");
                check(p.FindPdfMonitor(null) == null,
                    "production walk-template null lookup never reuses pending arbitrary add");
                int count = p._pdfSessions.Count;
                window.Open(row.Index);
                check(p._pdfSessions.Count == count, "production pending row opens existing owned session without duplication");
                File.Delete(source);
                p.SaveFails = false;
                window.Retry();
                check(!pending.IsPendingAdd && pending.CanLeave(out _) && window.Rows().All(r => r.Index >= 0),
                    "production Retry Save commits failed add without original source and refreshes status row");
                check(ReferenceEquals(p.FindPdfMonitor(pending.AttachmentIndex), pending),
                    "production committed add becomes ordinary indexed edit session");
                check(p.FinishOrientationEditing() && p._pdfSessions.Count == 0 && !File.Exists(pending.WorkingPath),
                    "production leave guard cleans saved add only after successful retry");
            };
            try { p.AttachmentsButton_Click(p, new System.Windows.RoutedEventArgs()); }
            finally { AttachmentsWindow.OnShow = null; }

            // Keep a live, stable edit registered while the active report changes.
            // Exercise both the timer filter and the captured save callback, not Cleanup's closed flag.
            var owner = p._currentInspection!;
            string ownerPath = p._currentFilePath!;
            var active = new PdfAttachmentService(owner).Open(0, ownerPath, root);
            p.RegisterPdfMonitor(active);
            byte[] edit = AutofillProbe.SyntheticPdf("stale report pending edit");
            File.WriteAllBytes(active.WorkingPath, edit);
            Func<bool> capturedSave = () => p.SavePdfMutation(owner, ownerPath);
            int savedBeforeSwitch = p.SaveCount;
            check(!active.Poll(DateTimeOffset.UtcNow.AddSeconds(-1), capturedSave) && active.HasPendingChanges,
                "production stale-report probe primes live pending edit beyond debounce");
            string ownerBefore = Newtonsoft.Json.JsonConvert.SerializeObject(owner);
            var newOwner = new InspectionFile { Attachments = new() {
                PdfAttachmentService.Create("new-owner.pdf", AutofillProbe.SyntheticPdf("new owner")) } };
            string newOwnerBefore = Newtonsoft.Json.JsonConvert.SerializeObject(newOwner);
            foreach (var change in new[] { "owner only", "path only", "owner and path" })
            {
                // New bytes clear the prior rejected-save state so each timer guard is exercised.
                edit = AutofillProbe.SyntheticPdf("pending edit for " + change);
                File.WriteAllBytes(active.WorkingPath, edit);
                check(!active.Poll(DateTimeOffset.UtcNow.AddSeconds(-1), capturedSave) && p.SaveCount == savedBeforeSwitch,
                    "production stale-report probe rearms stable edit for " + change);
                p._currentInspection = change == "path only" ? owner : newOwner;
                p._currentFilePath = change == "owner only" ? ownerPath : Path.Combine(root, "new-owner.ins");
                string statusBeforePoll = active.Status;
                p.PollPdfMonitors();
                check(p.SaveCount == savedBeforeSwitch && active.Status == statusBeforePoll && p.FindPdfMonitor(0) == null,
                    "production timer skips live stale monitor after switching " + change);
                check(!active.RetrySave(capturedSave) && p.SaveCount == savedBeforeSwitch,
                    "production captured callback never saves new active report after switching " + change);
                check(Newtonsoft.Json.JsonConvert.SerializeObject(owner) == ownerBefore && owner.AttachmentEdit == null &&
                    Newtonsoft.Json.JsonConvert.SerializeObject(newOwner) == newOwnerBefore && newOwner.AttachmentEdit == null &&
                    p._pdfSessions.Count == 1 && File.ReadAllBytes(active.WorkingPath).SequenceEqual(edit) && !active.CanLeave(out _),
                    "production stale-report rejection preserves both models and pending working copy for " + change);
            }
            p._currentInspection = owner;
            p._currentFilePath = ownerPath;
            check(active.RetrySave(capturedSave) && p.SaveCount == savedBeforeSwitch + 1 &&
                PdfAttachmentService.EmbeddedPdf((JObject)owner.Attachments![0]).SequenceEqual(edit) &&
                Newtonsoft.Json.JsonConvert.SerializeObject(newOwner) == newOwnerBefore,
                "production live stale monitor saves only after original owner and path restored");
            check(p.FinishOrientationEditing() && p._pdfSessions.Count == 0 && !File.Exists(active.WorkingPath),
                "production stale-report probe cleans working copy only after original-owner save");
        }
    }
}
