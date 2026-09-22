"""Execute production PDF leave/update control flow with only WPF/service logging stubs."""
from pathlib import Path
import re
ROOT = Path(__file__).resolve().parents[1]

def extract(source, name):
    match = re.search(r'        (?:private|internal) [^\n]+ ' + name + r'\([^\n]*\)\s*\{', source)
    assert match, name
    i, depth = match.end(), 1
    while depth:
        depth += (source[i] == '{') - (source[i] == '}')
        i += 1
    return source[match.start():i]

ui = (ROOT / 'MainWindow.OrientationPdf.cs').read_text()
main = (ROOT / 'MainWindow.xaml.cs').read_text()
code = '''// Generated from production MainWindow lifecycle; only WPF/logging glue is stubbed.
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
'''
code += extract(ui, 'FinishOrientationEditing') + '\n'
code += extract(main, 'TryPrepareForAppUpdate') + '\n}}\n'
(ROOT / 'tests/OrientationPdfHarness/OrientationUiProbe.Generated.cs').write_text(code)
print('Generated production monitored PDF leave/update control-flow probe')
