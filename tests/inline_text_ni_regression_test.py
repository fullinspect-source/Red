"""Layout source guards plus executed production click/value lifecycle.
No native WPF layout/focus claims: collaborators are minimal test doubles.
Run: python3 tests/inline_text_ni_regression_test.py
"""
from pathlib import Path
import re
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
SOURCE = (ROOT / 'MainWindow.xaml.cs').read_text()


def method(name):
    match = re.search(r'        private [^\n]+ ' + name + r'\([^\n]*\)\s*\{', SOURCE)
    assert match, name
    pos, depth = match.end(), 1
    while depth:
        depth += (SOURCE[pos] == '{') - (SOURCE[pos] == '}')
        pos += 1
    return SOURCE[match.start():pos]


class InlineTextNiTests(unittest.TestCase):
    def test_bounded_text_and_ni_share_old_footprint(self):
        body = method('CreateInlineStatusHeaderControl')
        free = body.split('if (options == null)')[1].split('if (options != null)')[0]
        self.assertIn('var valueEditor = new Grid', free)
        self.assertIn('MinWidth = 150', free)
        self.assertIn('MaxWidth = 220', free)
        self.assertIn('new GridLength(1, GridUnitType.Star)', free)
        self.assertIn('Width = GridLength.Auto', free)
        textbox = free.split('var valueBox = new TextBox')[1].split('};')[0]
        self.assertNotIn('MinWidth = 150', textbox)
        self.assertNotIn('MaxWidth = 220', textbox)
        self.assertIn('MinHeight = 34', textbox)
        self.assertEqual(free.count('AddInlineNiValueButtonIfNeeded('), 1)
        self.assertIn('AddInlineNiValueButtonIfNeeded(valueEditor, item, alwaysShow: true)', free)
        self.assertIn('Grid.SetColumn(niButton, 1)', free)
        self.assertIn('niButton.Margin = new Thickness(0)', free)
        self.assertIn('AddInlineClearValueButton(panel, item, alwaysShow: true)', free)
        for handler in ['GotKeyboardFocus', 'TextChanged', 'LostFocus']:
            self.assertIn('valueBox.' + handler + ' += InlineValueBox_' + handler, free)
        self.assertIn('valueBox.SetValue(InlineValueDisplayProperty, true)', free)
        self.assertIn('valueBox.MouseLeftButtonUp += (_, e) => e.Handled = true', free)

    def test_force_is_local_not_global_status_policy(self):
        helper = method('AddInlineNiValueButtonIfNeeded')
        self.assertIn('bool alwaysShow = false', helper)
        self.assertIn('if (!alwaysShow && !ShouldOfferInlineNiValueButton(item))', helper)
        self.assertIn('Tag = new InlineValueAction(item, "NI")', helper)
        self.assertIn('niButton.Click += InlineStatusButton_Click', helper)
        self.assertEqual(SOURCE.count('AddInlineNiValueButtonIfNeeded(valueEditor, item, alwaysShow: true)'), 1)
        body = method('CreateInlineStatusHeaderControl').split('if (options == null)')[0]
        self.assertEqual(body.count('AddInlineNiValueButtonIfNeeded(panel, item);'), 2)

    def test_extracted_click_lifecycle(self):
        code = '''using System;
using System.Collections.Generic;
using System.IO;
class Item { public string Value = ""; public string Comments = "keep"; public List<string> Pictures = new(){"photo"}; public bool IsPictureRequired = false; }
record InlineValueAction(Item Item, string Value);
class Button { public object Tag = null!; }
class RoutedEventArgs { public bool Handled; }
class SearchBox { public string Text = "filter"; }
class Probe {
 Item? owner; string editor = ""; bool dirty; int refreshes; SearchBox SearchFilterBox = new();
 void LoadItemEditor(Item item) { owner = item; editor = item.Value; }
 void SelectItemInTreeView(Item item) { if(owner != item) throw new Exception("ownership"); }
 void MarkUnsaved() { dirty = true; }
 void RecordInlineValueUsage(Item item, string value) { }
 void RefreshEngDataPanel() { refreshes++; }
 void RefreshEcDataPanel() { refreshes++; }
 void PopulateTreeView(string filter) { if(filter != "filter") throw new Exception("filter"); }
 void PromptClearOnPass(bool comments, bool pics) { throw new Exception("NI must not clear evidence"); }
 static void Check(bool ok, string text) { if(!ok) throw new Exception(text); }
 public static void Main() {
  foreach(var initial in new[]{"", "123", "Fail", "ni"}) {
   var p = new Probe(); var other = new Item { Value = "other" }; p.LoadItemEditor(other);
   var item = new Item { Value = initial }; var args = new RoutedEventArgs();
   p.InlineStatusButton_Click(new Button { Tag = new InlineValueAction(item, "NI") }, args);
   var expected = initial == "ni" ? "" : "NI";
   Check(item.Value == expected && p.editor == expected && p.owner == item, "value and editor mirror");
   Check(args.Handled && p.dirty && p.refreshes == 2, "dirty and refresh lifecycle");
   Check(other.Value == "other" && item.Comments == "keep" && item.Pictures.Count == 1, "ownership and evidence");
   // Disk roundtrip verifies the emitted value, not the application's INS serializer.
   var file = Path.GetTempFileName(); try { File.WriteAllText(file, item.Value); Check(File.ReadAllText(file) == expected, "value disk roundtrip"); } finally { File.Delete(file); }
  }
  Console.WriteLine("PASS: 4 extracted NI click/value lifecycle cases with disk roundtrip");
 }
'''
        code += method('InlineStatusButton_Click') + '\n' + method('SetInlineItemValue') + '\n}'
        with tempfile.TemporaryDirectory(prefix='red-inline-ni-') as temp:
            folder = Path(temp)
            (folder / 'Probe.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework><RollForward>Major</RollForward><Nullable>enable</Nullable></PropertyGroup></Project>')
            (folder / 'Program.cs').write_text(code)
            subprocess.run(['dotnet', 'run', '--project', str(folder / 'Probe.csproj')], check=True)


if __name__ == '__main__':
    unittest.main()
