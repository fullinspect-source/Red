"""Run actual OCR C# methods, including existing scope regressions, with UI-only stubs.
The MessageBox stub counts calls: the owner rule (2026-10-19) allows none on this path.
No live inspection files or API requests are involved.
"""
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]

class WindowRatingTests(unittest.TestCase):
    def test_actual_source(self):
        # Reuse the existing actual-source harness and its original checks unchanged.
        original = (ROOT / 'tests/ocr_unit_scope_test.py').read_text()
        code = original.split("code='''", 1)[1].split("'''", 1)[0]
        code = code.replace('public static void Main(){', 'public static void Main(){ WindowChecks();')
        code += r'''
 static void WindowChecks(){
  // Owner fix list 2026-10-19 p3: no "Confirm window ratings for both types" popup. The other window type's single
  // field gets the rating at once (blank, or still in sync with this field); its own rating and NI stay; changed
  // fields flash red (FlashChangedValues).
  const string rating="U-Factor: 0.29 / SHGC: 0.20";
  var p=new Probe();var a=I("Operable Windows SHGC/U-Factor (Grid and No Grid)");var b=I("Fixed Windows SHGC/U-Factor (Grid and No Grid)");
  void Reset(){a.Value=null;b.Value=null;p._readOnlyMode=false;p._currentInspection=new(){Sections=new(){new(){Items=new(){a,b}}}};MessageBox.Reset();p.flashed.Clear();}
  Reset();p.ApplyTranscriptionSuggestion(a,rating);Check((string?)a.Value==rating,"combined anchor keeps BOTH ratings");Check((string?)b.Value==rating,"blank partner filled at once");Check(MessageBox.Calls==0,"no confirmation popup");Check(p.flashed.Contains(a)&&p.flashed.Contains(b),"both changed fields flash");
  Reset();b.Value="U-Factor: 0.40 / SHGC: 0.30";p.ApplyTranscriptionSuggestion(a,rating);Check((string?)b.Value=="U-Factor: 0.40 / SHGC: 0.30"&&!p.flashed.Contains(b),"partner's own rating kept, not flashed");
  Reset();a.Value="U-Factor: 0.40 / SHGC: 0.30";b.Value="U-Factor: 0.40 / SHGC: 0.30";p.ApplyTranscriptionSuggestion(a,rating);Check((string?)b.Value==rating,"in-sync partner follows a new scan");
  Reset();a.Value="NI";b.Value="NI";p.ApplyTranscriptionSuggestion(a,rating);Check((string?)a.Value==rating&&(string?)b.Value=="NI","NI partner kept even when in sync");
  Reset();p.ApplyTranscriptionSuggestion(b,"SHGC = 0.20; U-Value = 0.29; VT: 0.47");Check((string?)a.Value==rating&&(string?)b.Value==rating,"reverse direction and aliases, no VT leakage");
  Reset();p.ApplyTranscriptionSuggestion(a,"U-Factor: 0.29 SHGC: 0.20");Check((string?)a.Value==rating,"missing separator preserves composite");
  Reset();p.ApplyTranscriptionSuggestion(a,"U-Factor: 0.29");Check((string?)a.Value=="U-Factor: 0.29"&&b.Value==null,"partial labeled rating not stripped or shared");
  Reset();p.ApplyTranscriptionSuggestion(a,"0.29");Check((string?)a.Value=="0.29"&&b.Value==null,"scalar cannot invent SHGC or share");
  Reset();p.ApplyTranscriptionSuggestion(a,"U-Factor: 0.29 / U-Factor: 0.40 / SHGC: 0.20");Check(b.Value==null&&((string?)a.Value)!.Contains("0.40"),"conflicting OCR stays intact, not shared");
  Reset();p._currentInspection!.Sections=new(){new(){Items=new(){a}},new(){Items=new(){b}}};p.ApplyTranscriptionSuggestion(a,rating);Check(b.Value==null,"no cross-section sharing");
  Reset();b.Name="Fixed Windows SHGC/U-Factor unit 2";p.ApplyTranscriptionSuggestion(a,rating);Check(b.Value==null,"no cross-unit sharing");b.Name="Fixed Windows SHGC/U-Factor";
  Reset();var b2=I(b.Name);p._currentInspection!.Sections[0].Items.Add(b2);p.ApplyTranscriptionSuggestion(a,rating);Check(b.Value==null&&b2.Value==null,"ambiguous partners not guessed");
  Reset();p._readOnlyMode=true;p.ApplyTranscriptionSuggestion(a,rating);Check(a.Value==null&&b.Value==null,"readonly composite rejected");
  Reset();p.ApplyTranscriptionSuggestion(I(a.Name),rating);Check(b.Value==null,"stale composite owner rejected");
  Reset();b.Value=rating;p.ApplyTranscriptionSuggestion(a,rating);Check((string?)a.Value==rating&&(string?)b.Value==rating&&!p.flashed.Contains(b),"already identical companion stays untouched");
  Check(MessageBox.Calls==0,"no popup in any case"); }
'''
        src = (ROOT / 'MainWindow.xaml.cs').read_text()
        start = src.index('        private string ApplyTranscriptionSuggestion(')
        end = src.index('        // ── MegaStats', start)
        code += src[start:end] + '\n}'
        code += '''
namespace System.Windows {
 enum MessageBoxButton {YesNo} enum MessageBoxImage {Question} enum MessageBoxResult {Yes,No,Cancel}
 static class MessageBox {
  public static int Calls;public static string Text="";public static MessageBoxResult Answer=MessageBoxResult.No,Default;public static Action? During;
  public static void Reset(){Calls=0;Text="";Answer=MessageBoxResult.No;During=null;}
  public static MessageBoxResult Show(string text,string title,MessageBoxButton buttons,MessageBoxImage icon,MessageBoxResult defaultResult){Calls++;Text=text;Default=defaultResult;During?.Invoke();return Answer;}
 }
}
'''
        with tempfile.TemporaryDirectory(dir=Path.home() / '.hermes/cache/scratch') as tmp:
            d = Path(tmp)
            (d / 'Program.cs').write_text(code)
            (d / 'Probe.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework><RollForward>Major</RollForward><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>')
            r = subprocess.run(['dotnet', 'run', '--project', str(d / 'Probe.csproj')], capture_output=True, text=True)
            self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
            print(r.stdout.strip())

if __name__ == '__main__':
    unittest.main()
