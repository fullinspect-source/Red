// Generated from production MainWindow code; WPF dialogs only are stubbed.
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
        private bool FinishOrientationEditing(bool leaving = true)
        {
            if (_orientationPdf == null) return true;
            if (_finishingOrientationPdf) return false;
            _finishingOrientationPdf = true;
            try
            {
                string guidance = "Save and close the PDF in your external editor before choosing Yes. RED cannot detect unsaved edits inside another app.\n\n";
                var result = MessageBox.Show(guidance + (leaving
                    ? "Yes: Save this working PDF into the INS and continue.\nNo: Don't save this PDF session and continue.\nCancel: Stay in this report. Other report edits keep their normal save safeguards."
                    : "Have you saved and closed the editor? Yes saves this working PDF into the INS. No cancels this save and keeps the PDF session open.") +
                    "\n\n" + _orientationPdf.WorkingPath, "Save Orientation PDF to INS",
                    leaving ? MessageBoxButton.YesNoCancel : MessageBoxButton.YesNo, MessageBoxImage.Question,
                    leaving ? MessageBoxResult.Cancel : MessageBoxResult.No);
                var decision = result == MessageBoxResult.Yes ? OrientationPdfDecision.Save
                    : leaving && result == MessageBoxResult.No ? OrientationPdfDecision.Discard : OrientationPdfDecision.Cancel;
                if (!_orientationPdf.TryFinish(decision, TrySaveCurrentInspection)) return false;
                _orientationPdf = null;
                UpdateOrientationPdfControls();
                return true;
            }
            catch (Exception ex) { ShowOrientationError(ex); return false; }
            finally { _finishingOrientationPdf = false; }
        }
        private void SaveOrientationPdfButton_Click(object sender, RoutedEventArgs e)
        {
            if (FinishOrientationEditing(leaving: false)) UpdateOrientationPdfControls();
        }
        internal bool TryPrepareForAppUpdate()
        {
            SyncCurrentItemFromUI();
            if (!FinishOrientationEditing()) return false;
            return !_hasUnsavedChanges || TrySaveCurrentInspection();
        }
}}
