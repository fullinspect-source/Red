"""Execute the production Orientation prompt/decision code with only WPF dialog stubs."""
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
code = '''// Generated from production MainWindow code; WPF dialogs only are stubbed.
using InspectionEditor.Services;
using InspectionEditor.Models;
using Newtonsoft.Json.Linq;
namespace OrientationControlFlow {
class RoutedEventArgs : EventArgs { }
enum MessageBoxButton { YesNoCancel, YesNo }
enum MessageBoxImage { Question }
enum MessageBoxResult { Yes, No, Cancel }
static class MessageBox {
 public static MessageBoxResult Result;
 public static int Prompts;
 public static MessageBoxButton Buttons;
 public static MessageBoxResult Show(string text, string title, MessageBoxButton buttons, MessageBoxImage icon, MessageBoxResult defaultResult) {
  Prompts++; Buttons = buttons; return Result;
 }
}
public class OrientationUiProbe {
 OrientationPdfSession? _orientationPdf;
 bool _finishingOrientationPdf, _hasUnsavedChanges, saveFails;
 int saves, errors;
 void UpdateOrientationPdfControls() { }
 void ShowOrientationError(Exception ex) { errors++; }
 void SyncCurrentItemFromUI() { }
 bool TrySaveCurrentInspection() { saves++; if (saveFails) return false; _hasUnsavedChanges = false; return true; }
 public static void Run(string root, Action<bool,string> check) {
  OrientationUiProbe Create() {
   var model = new InspectionFile { InspectionCode = "BWT", Attachments = new() {
    new JObject { ["Filename"] = "Orientation.pdf", ["FileData"] = Convert.ToBase64String(AutofillProbe.SyntheticPdf("existing")) }
   }};
   return new() { _orientationPdf = OrientationPdfSession.OpenEmbedded(model, Path.Combine(root,"ui.ins"), 0, root) };
  }
  var p = Create(); MessageBox.Result = MessageBoxResult.No;
  int prompts = MessageBox.Prompts;
  check(p.FinishOrientationEditing() && p._orientationPdf == null && p.saves == 0, "production leave No discards and clears active session");
  check(p.FinishOrientationEditing() && MessageBox.Prompts == prompts + 1, "production discard does not repeatedly prompt");
  check(MessageBox.Buttons == MessageBoxButton.YesNoCancel, "production leave offers three outcomes");
  p = Create(); MessageBox.Result = MessageBoxResult.Cancel;
  check(!p.FinishOrientationEditing() && p._orientationPdf != null && p.saves == 0, "production leave Cancel blocks transition");
  p._orientationPdf!.Discard();
  p = Create(); MessageBox.Result = MessageBoxResult.No;
  p.SaveOrientationPdfButton_Click(new object(), new RoutedEventArgs());
  check(p._orientationPdf != null && File.Exists(p._orientationPdf.WorkingPath) && p.saves == 0 && MessageBox.Buttons == MessageBoxButton.YesNo, "production explicit Save No retains session, no discard or exit");
  p._orientationPdf!.Discard();
  p = Create(); MessageBox.Result = MessageBoxResult.Yes;
  check(p.FinishOrientationEditing() && p._orientationPdf == null && p.saves == 1, "production leave Yes saves and clears session");
  p = Create(); MessageBox.Result = MessageBoxResult.Yes; p.saveFails = true;
  check(!p.FinishOrientationEditing() && p._orientationPdf != null && File.Exists(p._orientationPdf.WorkingPath), "production save failure retains session and blocks transition");
  p._orientationPdf!.Discard();
  p = Create(); MessageBox.Result = MessageBoxResult.No; p._hasUnsavedChanges = true;
  check(p.TryPrepareForAppUpdate() && p._orientationPdf == null && p.saves == 1, "production update after PDF discard saves ordinary dirty changes");
  p = Create(); MessageBox.Result = MessageBoxResult.No; p._hasUnsavedChanges = true; p.saveFails = true;
  check(!p.TryPrepareForAppUpdate() && p._orientationPdf == null && p._hasUnsavedChanges, "ordinary failed save still blocks update after PDF discard");
  p = Create(); MessageBox.Result = MessageBoxResult.Yes; File.Delete(p._orientationPdf!.WorkingPath);
  check(!p.FinishOrientationEditing() && p._orientationPdf != null && p.errors == 1, "missing working PDF blocks Yes with honest error");
  MessageBox.Result = MessageBoxResult.No;
  check(p.FinishOrientationEditing() && p._orientationPdf == null, "No leaves even when working PDF is missing");
 }
'''
code += extract(ui, 'FinishOrientationEditing') + '\n'
code += extract(ui, 'SaveOrientationPdfButton_Click') + '\n'
code += extract(main, 'TryPrepareForAppUpdate') + '\n}}\n'
(ROOT / 'tests/OrientationPdfHarness/OrientationUiProbe.Generated.cs').write_text(code)
print('Generated production Orientation save/leave/update prompt control-flow probe')
