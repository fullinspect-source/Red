using System.ComponentModel;
using System;
using InspectionEditor.Models;
namespace LifecycleControlFlow {
class RoutedEventArgs : EventArgs { }
enum MessageBoxButton { YesNoCancel, OK }
enum MessageBoxImage { Question, Error }
enum MessageBoxResult { Yes, No, Cancel }
static class MessageBox {
 public static MessageBoxResult Result = MessageBoxResult.No;
 public static MessageBoxResult Show(string text, string title, MessageBoxButton buttons, MessageBoxImage icon) => Result;
}
static class DiagnosticLogService { public static int Count; public static void Log(string context, Exception error) { Count++; } }
class Activity { public int Closes; public void LogClose() => Closes++; }
class Camera { public event Action? PhotoCaptured; public void StopSession() { } }
public class LifecycleProbe {
 bool _hasUnsavedChanges, _savingEditorChanges, _skipResultCheck, _pdfSaveInProgress;
 InspectionFile? _currentInspection = new();
 Activity _activityService = new(); Camera _cameraService = new();
 bool pending, fail, summary, unlocked, orientationActive, orientationReady = true; int writes;
 bool orientationDirty, cleanedAfterSave; int cleanups;
 // Model monitor polling/save separately from final session cleanup, as production does.
 bool FinishOrientationEditing(bool leaving = true) {
  if (!orientationActive) return true;
  if (!orientationReady) return false;
  if (orientationDirty) {
   _hasUnsavedChanges = true;
   _pdfSaveInProgress = true;
   try { if (!TrySaveCurrentInspection()) return false; orientationDirty = false; }
   finally { _pdfSaveInProgress = false; }
  }
  if (leaving) {
   cleanedAfterSave = !_hasUnsavedChanges && !pending && writes > 0;
   orientationActive = false;
   cleanups++;
  }
  return true;
 }
 void SyncCurrentItemFromUI() { if (pending) { _hasUnsavedChanges = true; pending = false; } }
 bool ShouldPromptForTradeSummaryOnClose() => summary;
 void GenerateSummaryInternal() { }
 void SaveCurrentInspectionInPlace() { SyncCurrentItemFromUI(); if (fail) throw new System.IO.IOException("simulated disk failure"); writes++; _hasUnsavedChanges = false; }
 void MarkUnsaved() => _hasUnsavedChanges = true;
 bool ShouldShowResultPicker() => false;
 bool ShowResultPicker(bool closeAfterPicker) => true;
 void SavePreferences() { }
 void ReleaseInspectionLocks() => unlocked = true;
 void OnPhotoCaptured() { }
 public static void Run() {
  void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS lifecycle " + name); }
  var p = new LifecycleProbe { pending = true };
  var e = new CancelEventArgs(); p.MainWindow_Closing(null,e);
  Check(p.writes == 1 && p.unlocked && !e.Cancel, "still-focused edit flushed before dirty gate");
  p = new LifecycleProbe { pending = true, fail = true }; e = new(); p.MainWindow_Closing(null,e);
  Check(DiagnosticLogService.Count == 1 && e.Cancel && p._hasUnsavedChanges && !p.unlocked && p._activityService.Closes == 0, "failed close retains dirty state and locks");
  p.fail = false; e = new(); p.MainWindow_Closing(null,e);
  Check(!e.Cancel && p.writes == 1 && !p._hasUnsavedChanges && p.unlocked, "retry closes only after successful save");
  p = new LifecycleProbe { pending = true, summary = true }; MessageBox.Result = MessageBoxResult.Cancel;
  e = new(); p.MainWindow_Closing(null,e);
  Check(e.Cancel && p._hasUnsavedChanges && !p.unlocked && p.writes == 0, "summary cancellation retains captured edits");
  MessageBox.Result = MessageBoxResult.No;
  p = new LifecycleProbe { orientationActive = true, orientationReady = false }; e = new(); p.MainWindow_Closing(null,e);
  Check(e.Cancel && !p.unlocked && p.writes == 0, "active PDF editor blocks close even when report is clean");
  p = new LifecycleProbe { orientationActive = true, orientationDirty = true, fail = true }; e = new(); p.MainWindow_Closing(null,e);
  Check(e.Cancel && p._hasUnsavedChanges && !p.unlocked, "PDF save failure blocks close and retains dirty state");
  p = new LifecycleProbe { pending = true, summary = true, orientationActive = true };
  MessageBox.Result = MessageBoxResult.Cancel; e = new(); p.MainWindow_Closing(null,e);
  Check(e.Cancel && p.orientationActive && p.cleanups == 0 && p._hasUnsavedChanges && p.writes == 0 && !p.unlocked && p._activityService.Closes == 0,
   "summary cancellation retains PDF session without cleanup");
  MessageBox.Result = MessageBoxResult.No; e = new(); p.MainWindow_Closing(null,e);
  Check(!e.Cancel && !p.orientationActive && p.cleanups == 1 && p.cleanedAfterSave && p.writes == 1 && !p._hasUnsavedChanges && p.unlocked && p._activityService.Closes == 1,
   "successful close cleans PDF session only after accepted leave and save");
  Console.WriteLine("8 extracted lifecycle checks passed");
 }
        private void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            SyncCurrentItemFromUI();
            if (!FinishOrientationEditing(leaving: false)) { e.Cancel = true; return; }
            // Before close, offer the same trade-summary generation that the Save button provides.
            if (_hasUnsavedChanges && _currentInspection != null && ShouldPromptForTradeSummaryOnClose())
            {
                var summaryResult = MessageBox.Show(
                    "Items with [trade] prefixes detected.\n\n" +
                    "• Yes = Generate timestamped summary and close\n" +
                    "• No = Close without new summary",
                    "Generate New Summary?",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Question);

                if (summaryResult == MessageBoxResult.Cancel)
                {
                    e.Cancel = true;
                    return;
                }

                if (summaryResult == MessageBoxResult.Yes)
                {
                    SyncCurrentItemFromUI();
                    GenerateSummaryInternal();
                    _hasUnsavedChanges = true;
                }
            }

            // Save first so result/status data is current before deciding whether to prompt.
            if (_hasUnsavedChanges && _currentInspection != null)
            {
                if (!TrySaveCurrentInspection())
                {
                    e.Cancel = true;
                    return;
                }
            }

            // Show result picker if this session had edits and result is not yet set
            if (!_skipResultCheck && ShouldShowResultPicker())
            {
                e.Cancel = true;
                ShowResultPicker(closeAfterPicker: true);
                return;
            }

            if (!FinishOrientationEditing()) { e.Cancel = true; return; }
            // Log activity: inspection closed
            _activityService.LogClose();
            SavePreferences();

            // Release multi-instance locks
            ReleaseInspectionLocks();

            // Clean up camera session
            _cameraService.PhotoCaptured -= OnPhotoCaptured;
            _cameraService.StopSession();
        }
        private bool TrySaveCurrentInspection()
        {
            if (_savingEditorChanges) return false;
            _savingEditorChanges = true;
            try
            {
                SaveCurrentInspectionInPlace();
                return true;
            }
            catch (Exception ex)
            {
                // Keep both the model and dirty state available for retry. Never reset/close on failure.
                DiagnosticLogService.Log("Report save failed; edits retained", ex);
                MarkUnsaved();
                if (!_pdfSaveInProgress) MessageBox.Show($"Your changes could not be saved. The report is still open with your edits.\n\n{ex.Message}",
                    "Report not saved", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
            finally { _savingEditorChanges = false; }
        }
}}
