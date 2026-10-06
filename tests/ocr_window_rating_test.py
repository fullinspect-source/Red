"""Run actual OCR C# methods, including existing scope regressions, with UI-only stubs.
The MessageBox stub records the preview and simulates explicit Yes/No/close choices.
No live inspection files or API requests are involved.
"""
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]

class WindowRatingTests(unittest.TestCase):
    def test_actual_source(self):
        # Reuse the existing actual-source harness and its 13 original checks unchanged.
        original = (ROOT / 'tests/ocr_unit_scope_test.py').read_text()
        code = original.split("code='''", 1)[1].split("'''", 1)[0]
        code = code.replace('public static void Main(){', 'public static void Main(){ WindowChecks();')
        code += r'''
 static void WindowChecks(){
  const string rating="U-Factor: 0.29 / SHGC: 0.20";
  var p=new Probe();var a=I("Operable Windows SHGC/U-Factor (Grid and No Grid)");var b=I("Fixed Windows SHGC/U-Factor (Grid and No Grid)");
  void Reset(){a.Value=null;b.Value=null;p._readOnlyMode=false;p._currentInspection=new(){Sections=new(){new(){Items=new(){a,b}}}};MessageBox.Reset();}
  Reset();p.ApplyTranscriptionSuggestion(a,rating);Check((string?)a.Value==rating,"combined anchor keeps BOTH ratings");Check(b.Value==null,"blank fixed NOT inferred without Yes");Check(MessageBox.Calls==1,"blank partner offered explicit choice");
  Check(MessageBox.Text.Contains("(blank)")&&MessageBox.Text.Contains(rating)&&MessageBox.Text.Contains("does not prove"),"preview exposes old/new and evidence warning");Check(MessageBox.Default==MessageBoxResult.No,"safe No default");
  Reset();MessageBox.Answer=MessageBoxResult.Yes;p.ApplyTranscriptionSuggestion(a,rating);Check((string?)b.Value==rating,"explicit Yes shares complete rating");
  Reset();b.Value="U-Factor: 0.40 / SHGC: 0.30";p.ApplyTranscriptionSuggestion(a,rating);Check((string?)b.Value=="U-Factor: 0.40 / SHGC: 0.30","existing fixed preserved on No");Check(MessageBox.Text.Contains("0.40")&&MessageBox.Text.Contains("0.29"),"overwrite preview shows old AND new");
  Reset();b.Value="NI";MessageBox.Answer=MessageBoxResult.Cancel;p.ApplyTranscriptionSuggestion(a,rating);Check((string?)b.Value=="NI","close/cancel preserves NI");
  Reset();b.Value="old";MessageBox.Answer=MessageBoxResult.Yes;p.ApplyTranscriptionSuggestion(a,rating);Check((string?)b.Value==rating,"overwrite only explicit Yes");
  Reset();MessageBox.Answer=MessageBoxResult.Yes;p.ApplyTranscriptionSuggestion(b,"SHGC = 0.20; U-Value = 0.29; VT: 0.47");Check((string?)a.Value==rating&&(string?)b.Value==rating,"reverse direction and aliases, no VT leakage");
  Reset();p.ApplyTranscriptionSuggestion(a,"U-Factor: 0.29 SHGC: 0.20");Check((string?)a.Value==rating,"missing separator preserves composite");
  Reset();p.ApplyTranscriptionSuggestion(a,"U-Factor: 0.29");Check((string?)a.Value=="U-Factor: 0.29"&&MessageBox.Calls==0,"partial labeled rating not stripped or shared");
  Reset();p.ApplyTranscriptionSuggestion(a,"0.29");Check((string?)a.Value=="0.29"&&b.Value==null&&MessageBox.Calls==0,"scalar cannot invent SHGC or share");
  Reset();p.ApplyTranscriptionSuggestion(a,"U-Factor: 0.29 / U-Factor: 0.40 / SHGC: 0.20");Check(MessageBox.Calls==0&&((string?)a.Value)!.Contains("0.40"),"conflicting OCR stays intact, not shared");
  Reset();p._currentInspection!.Sections=new(){new(){Items=new(){a}},new(){Items=new(){b}}};p.ApplyTranscriptionSuggestion(a,rating);Check(b.Value==null&&MessageBox.Calls==0,"no cross-section sharing");
  Reset();b.Name="Fixed Windows SHGC/U-Factor unit 2";p.ApplyTranscriptionSuggestion(a,rating);Check(b.Value==null&&MessageBox.Calls==0,"no cross-unit sharing");b.Name="Fixed Windows SHGC/U-Factor";
  Reset();p._currentInspection!.Sections[0].Items.Add(I(b.Name));p.ApplyTranscriptionSuggestion(a,rating);Check(MessageBox.Calls==0,"ambiguous partners not offered");
  Reset();MessageBox.Answer=MessageBoxResult.Yes;MessageBox.During=()=>b.Value="new manual edit";p.ApplyTranscriptionSuggestion(a,rating);Check((string?)b.Value=="new manual edit","late edit not overwritten");
  Reset();MessageBox.Answer=MessageBoxResult.Yes;MessageBox.During=()=>p._currentInspection=new();p.ApplyTranscriptionSuggestion(a,rating);Check(b.Value==null,"late owner switch rejected");
  Reset();p._readOnlyMode=true;p.ApplyTranscriptionSuggestion(a,rating);Check(a.Value==null&&MessageBox.Calls==0,"readonly composite rejected");
  Reset();p.ApplyTranscriptionSuggestion(I(a.Name),rating);Check(MessageBox.Calls==0,"stale composite owner rejected");
  Reset();b.Value=rating;p.ApplyTranscriptionSuggestion(a,rating);Check(MessageBox.Calls==0&&(string?)a.Value==rating,"already identical companion stays untouched");
 }
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
