"""Extract actual close/failure-gate method bodies; stub only WPF dependencies.
This runs control-flow regression tests on macOS, NOT Windows focus/dispatch tests.
"""
from pathlib import Path
import re
root = Path(__file__).resolve().parents[1]
src = (root / 'MainWindow.xaml.cs').read_text()
def extract(name):
    m = re.search(r'        private [^\n]+ ' + name + r'\([^\n]*\)\s*\{', src)
    assert m, name
    i, depth = m.end(), 1
    while depth:
        depth += (src[i] == '{') - (src[i] == '}')
        i += 1
    return src[m.start():i]
code = '''using System.ComponentModel;
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
 bool _hasUnsavedChanges, _savingEditorChanges, _skipResultCheck;
 InspectionFile? _currentInspection = new();
 Activity _activityService = new(); Camera _cameraService = new();
 bool pending, fail, summary, unlocked, orientationActive, orientationReady = true; int writes;
 bool FinishOrientationEditing() { if (!orientationActive) return true; if (!orientationReady) return false; _hasUnsavedChanges = true; return TrySaveCurrentInspection(); }
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
  p = new LifecycleProbe { orientationActive = true, fail = true }; e = new(); p.MainWindow_Closing(null,e);
  Check(e.Cancel && p._hasUnsavedChanges && !p.unlocked, "PDF save failure blocks close and retains dirty state");
  Console.WriteLine("6 extracted lifecycle checks passed");
 }
'''
code += extract('MainWindow_Closing') + '\n' + extract('TrySaveCurrentInspection') + '\n}}\n'
(root / 'tests/SaveLossHarness/LifecycleProbe.Generated.cs').write_text(code)
print('Generated actual close/save gate methods for control-flow harness')
