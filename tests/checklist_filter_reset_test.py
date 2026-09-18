"""Run real filter handlers extracted from MainWindow against small WPF doubles.
Usage: python3 tests/checklist_filter_reset_test.py (requires .NET 10 SDK).
No production files or build outputs are written by this test.
"""
from pathlib import Path
import re
import subprocess
import tempfile

SOURCE = (Path(__file__).resolve().parents[1] / 'MainWindow.xaml.cs').read_text()

def method(name):
    match = re.search(r'        private void ' + name + r'\([^\n]*\)\s*\{', SOURCE)
    assert match, name
    start = match.start()
    pos = match.end()
    depth = 1
    while depth:
        depth += (SOURCE[pos] == '{') - (SOURCE[pos] == '}')
        pos += 1
    return SOURCE[start:pos]

names = ['ResetChecklistFilters', 'SearchFilterBox_TextChanged', 'ClearSearchButton_Click',
         'OfiFilterButton_Click', 'ReqFilterButton_Click', 'IncFilterButton_Click']
methods = '\n'.join(method(n) for n in names)
for label in ['Ofi', 'Req', 'Inc']:
    body = method(label + 'FilterButton_Click')
    assert body.index('ResetChecklistFilters();') < body.index('_' + label.lower() + 'FilterActive = true;')
    assert '= !' not in body
    assert body.count('PopulateTreeView(') == 1
assert 'BeginInvoke' not in method('ClearSearchButton_Click')
reset = method('ResetChecklistFilters')
for label in ['ofi', 'req', 'inc']:
    assert '_' + label + 'FilterActive = false;' in reset
assert 'finally { _resettingChecklistFilters = false; }' in reset
assert 'if (_resettingChecklistFilters) return;' in method('SearchFilterBox_TextChanged')
assert '_checklistFilterGeneration++;' in method('PopulateTreeView')
assert 'CaptureCommentEdit' in SOURCE and 'CaptureValueEdit' in SOURCE
# Only these three user-selectable boolean filters exist in the tree renderer.
assert set(re.findall(r'_\w+FilterActive', method('PopulateTreeView'))) == {'_ofiFilterActive', '_reqFilterActive', '_incFilterActive'}

prefix = r'''
using System;
using System.Collections.Generic;
namespace System.Windows.Threading { enum DispatcherPriority { Background, Loaded } }
class RoutedEventArgs {} class TextChangedEventArgs {}
class Item { public object Value = "fail"; public string Comments = "unsaved edit"; }
class Section {}
class TreeViewItem {
 public object Tag; public List<TreeViewItem> Items = new();
 public bool IsExpanded, IsSelected; public void BringIntoView() {}
}
class SearchBox {
 string text = ""; public Action Changed;
 public string Text { get => text; set { if (text == value) return; text = value; Changed(); } }
 public int FocusCount; public void Focus() { FocusCount++; }
}
class QueueDispatcher {
 public Queue<Action> Queue = new();
 public void BeginInvoke(Action a, System.Windows.Threading.DispatcherPriority p) { Queue.Enqueue(a); }
 public void Drain() { while (Queue.Count > 0) Queue.Dequeue()(); }
}
class Harness {
 bool _ofiFilterActive, _reqFilterActive, _incFilterActive, _resettingChecklistFilters;
 int _checklistFilterGeneration;
 object _currentInspection = new();
 SearchBox SearchFilterBox = new();
 QueueDispatcher Dispatcher = new();
 TreeViewItem SectionsTreeView = new();
 Item edited = new(); int renders, navigations;
 string renderedSearch; int renderedMode;
 void UpdateChecklistFilterButtonStyles() {}
 void LoadItemEditor(Item item) { navigations++; Assert(item.Comments == "unsaved edit", "edit retained"); }
 void PopulateTreeView(string filter = null) {
  _checklistFilterGeneration++;
  renders++; renderedSearch = filter; renderedMode = Mode();
  SectionsTreeView.Items.Clear();
  var section = new TreeViewItem { Tag = new Section() };
  section.Items.Add(new TreeViewItem { Tag = edited });
  SectionsTreeView.Items.Add(section);
 }
 int Mode() => (_ofiFilterActive ? 1 : 0) + (_reqFilterActive ? 2 : 0) + (_incFilterActive ? 4 : 0);
 void Click(int mode) {
  if (mode == 0) ClearSearchButton_Click(null, null);
  if (mode == 1) OfiFilterButton_Click(null, null);
  if (mode == 2) ReqFilterButton_Click(null, null);
  if (mode == 4) IncFilterButton_Click(null, null);
 }
 static int assertions;
 static void Assert(bool ok, string message) { assertions++; if (!ok) throw new Exception(message); }
 public Harness() { SearchFilterBox.Changed = () => SearchFilterBox_TextChanged(null, null); }
 public static void Main() {
  // Every prior mode, with/without search, every destination, repeated selection.
  foreach (int previous in new[] {0,1,2,4})
  foreach (string search in new[] {"", "nonmatching query"})
  foreach (int next in new[] {0,1,2,4}) {
   var h = new Harness(); h.Click(previous); h.SearchFilterBox.Text = search;
   int before = h.renders; h.Click(next);
   Assert(h.Mode() == next && h.SearchFilterBox.Text == "", "exclusive reset");
   Assert(h.renders == before + 1, "single atomic render");
   h.Click(next); h.Dispatcher.Drain();
   Assert(h.Mode() == next && h.renderedMode == next && h.renderedSearch == "", "repeat stays active");
   Assert(h.edited.Comments == "unsaved edit", "preserve edits");
   Assert(h.navigations == (next == 1 ? 1 : 0), "only latest OFI navigates");
  }
  // ALL has no delayed render or delayed focus to override the next action.
  foreach (int next in new[] {1,2,4}) {
   var h = new Harness(); h.Click(0); h.Click(next);
   int before = h.renders, focus = h.SearchFilterBox.FocusCount;
   h.Dispatcher.Drain();
   Assert(h.renders == before && h.SearchFilterBox.FocusCount == focus && h.Mode() == next, "rapid ALL transition");
  }
  var s = new Harness(); s.Click(1); s.SearchFilterBox.Text = "new search"; s.Dispatcher.Drain();
  Assert(s.navigations == 0, "search cancels stale OFI");
  var r = new Harness(); r.Click(1); r._currentInspection = new(); r.Dispatcher.Drain();
  Assert(r.navigations == 0, "report switch cancels stale OFI");
  var q = new Harness(); q.Click(1); q.Click(2); q.Click(1); q.Dispatcher.Drain();
  Assert(q.navigations == 1, "OFI REQ OFI generation guard");
  Console.WriteLine($"PASS: {assertions} runtime assertions; extracted production filter handlers");
 }
'''
with tempfile.TemporaryDirectory(prefix='red-filter-test-') as directory:
    root = Path(directory)
    (root / 'Harness.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><EnableDefaultCompileItems>true</EnableDefaultCompileItems></PropertyGroup></Project>')
    (root / 'Program.cs').write_text(prefix + methods + '\n}\n')
    subprocess.run(['dotnet', 'run', '--project', str(root / 'Harness.csproj'), '--configuration', 'Release'], check=True)
print('PASS: source guards for ALL, OFI, REQ, INC, search suppression, render invalidation and filter inventory')
