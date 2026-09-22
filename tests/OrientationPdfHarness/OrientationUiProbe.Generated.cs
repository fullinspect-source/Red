// Generated from production MainWindow lifecycle; only WPF/logging glue is stubbed.
#nullable enable
using InspectionEditor.Services;
using InspectionEditor.Models;
using Newtonsoft.Json.Linq;
namespace OrientationControlFlow {
enum MessageBoxButton { OK }
enum MessageBoxImage { Warning }
static class MessageBox {
 public static int Prompts;
 public static void Show(string text, string title, MessageBoxButton buttons, MessageBoxImage icon) { Prompts++; }
}
static class DiagnosticLogService { public static void Log(string text, Exception ex) { } }
class TimerStub { public bool Running = true; public void Start() { Running = true; } public void Stop() { Running = false; } }
public class OrientationUiProbe {
 record Entry(PdfEditMonitor Monitor);
 readonly List<Entry> _pdfSessions = new();
 readonly TimerStub _pdfTimer = new();
 PdfEditMonitor? _orientationPdf;
 bool _finishingOrientationPdf, _pdfPolling, _hasUnsavedChanges, saveFails;
 int saves;
 DateTimeOffset now = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(1);
 void UpdateOrientationPdfControls() { }
 void SyncCurrentItemFromUI() { }
 bool TrySaveCurrentInspection() { saves++; if (saveFails) return false; _hasUnsavedChanges = false; return true; }
 void PollPdfMonitors() { foreach (var entry in _pdfSessions) entry.Monitor.Poll(now, TrySaveCurrentInspection); }
 public static void Run(string root, Action<bool,string> check) {
  OrientationUiProbe Create(bool general = false) {
   var model = new InspectionFile { InspectionCode = "BWT", Attachments = new() {
    new JObject { ["Filename"] = "Example.pdf", ["FileData"] = Convert.ToBase64String(AutofillProbe.SyntheticPdf("existing")) }
   }};
   var monitor = new PdfAttachmentService(model).Open(0, Path.Combine(root,"ui.ins"), root);
   var p = new OrientationUiProbe(); p._pdfSessions.Add(new Entry(monitor));
   if (!general) p._orientationPdf = monitor;
   return p;
  }
  void Change(OrientationUiProbe p) {
   File.WriteAllBytes(p._pdfSessions[0].Monitor.WorkingPath, AutofillProbe.SyntheticPdf("changed"));
  }
  var p = Create(); int prompts = MessageBox.Prompts;
  check(p.FinishOrientationEditing() && p._pdfSessions.Count == 0 && p.saves == 0,
   "production unchanged leave cleans sessions without save");
  check(MessageBox.Prompts == prompts && !p._pdfTimer.Running,
   "production unchanged leave has no PDF confirmation and stops timer");
  p = Create(); Change(p);
  check(!p.FinishOrientationEditing() && p._pdfSessions.Count == 1 && p._pdfTimer.Running,
   "production unstable PDF blocks leave and retains monitored session");
  check(File.Exists(p._pdfSessions[0].Monitor.WorkingPath), "production pending working copy retained");
  p.now += TimeSpan.FromSeconds(1);
  check(p.FinishOrientationEditing() && p.saves == 1 && p._pdfSessions.Count == 0,
   "production stable disk change saves once then leaves");
  p = Create(general: true); Change(p); p.saveFails = true;
  p.FinishOrientationEditing(); p.now += TimeSpan.FromSeconds(1);
  check(!p.FinishOrientationEditing() && p.saves == 1 && p._pdfSessions.Count == 1,
   "production general PDF failed save also blocks report leave");
  p.now += TimeSpan.FromSeconds(1); p.FinishOrientationEditing();
  check(p.saves == 1, "production failed bytes do not repeatedly retry timer saves");
  p.saveFails = false;
  p._pdfSessions[0].Monitor.RetrySave(p.TrySaveCurrentInspection);
  check(p.FinishOrientationEditing() && p.saves == 2,
   "production manual retry lets general PDF leave");
  p = Create();
  check(p.FinishOrientationEditing(leaving: false) && p._pdfSessions.Count == 1 && p._pdfTimer.Running,
   "production non-leaving report save retains PDF monitoring");
  p._hasUnsavedChanges = true;
  check(p.TryPrepareForAppUpdate() && p.saves == 1 && p._pdfSessions.Count == 1 && p._pdfTimer.Running,
   "production update preserves ordinary report dirty save");
  p.FinishOrientationEditing();
  p = Create(); p._hasUnsavedChanges = true; p.saveFails = true;
  check(!p.TryPrepareForAppUpdate() && p._hasUnsavedChanges && p._pdfSessions.Count == 1 && p._pdfTimer.Running,
   "ordinary report save failure still blocks update");
  p.FinishOrientationEditing();
  p = Create(); var originalBytes = File.ReadAllBytes(p._pdfSessions[0].Monitor.WorkingPath);
  File.Delete(p._pdfSessions[0].Monitor.WorkingPath);
  check(!p.FinishOrientationEditing() && p._pdfSessions.Count == 1 && p._pdfTimer.Running,
   "production missing working PDF blocks without discard or uncaught error");
  File.WriteAllBytes(p._pdfSessions[0].Monitor.WorkingPath, originalBytes);
  check(p.FinishOrientationEditing(), "production restored unchanged working bytes permit leave");
 }
        private bool FinishOrientationEditing(bool leaving = true)
        {
            if (_finishingOrientationPdf || _pdfPolling) return false;
            _finishingOrientationPdf = true;
            try
            {
                PollPdfMonitors();
                foreach (var entry in _pdfSessions)
                {
                    if (!entry.Monitor.CanLeave(out string reason))
                    {
                        MessageBox.Show($"{reason}\n\nFinish/save/close the PDF in its editor, then use ATTACHMENTS > Retry Save if needed. " +
                            "RED cannot detect unsaved edits held only inside another app.\n\nWorking copy retained:\n" + entry.Monitor.WorkingPath,
                            "PDF changes still pending", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return false;
                    }
                }
                if (leaving)
                {
                    // Cleanup rechecks disk and can detect a late write/lock. Remove only sessions
                    // actually cleaned so a partial cleanup can safely resume the remaining monitors.
                    foreach (var entry in _pdfSessions.ToArray())
                    {
                        entry.Monitor.Cleanup();
                        _pdfSessions.Remove(entry);
                        if (ReferenceEquals(_orientationPdf, entry.Monitor)) _orientationPdf = null;
                    }
                    _pdfTimer?.Stop();
                    _orientationPdf = null;
                    UpdateOrientationPdfControls();
                }
                return true;
            }
            catch (Exception ex)
            {
                if (_pdfSessions.Count > 0) _pdfTimer?.Start();
                DiagnosticLogService.Log("PDF completion interrupted; remaining working copies retained", ex);
                MessageBox.Show(ex.Message + "\n\nPDF monitoring remains active. Finish/save/close the editor and use ATTACHMENTS > Retry Save.\n\nRetained working copies:\n" +
                    string.Join("\n", _pdfSessions.Select(entry => entry.Monitor.WorkingPath)),
                    "PDF changes still pending", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }
            finally { _finishingOrientationPdf = false; }
        }
        internal bool TryPrepareForAppUpdate()
        {
            SyncCurrentItemFromUI();
            if (!FinishOrientationEditing(leaving: false)) return false;
            return !_hasUnsavedChanges || TrySaveCurrentInspection();
        }
}}
