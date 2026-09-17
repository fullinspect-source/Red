"""Execute actual WPF OCR distribution methods with UI-only stubs on macOS."""
from pathlib import Path
import re, subprocess, tempfile, unittest
ROOT=Path(__file__).resolve().parents[1]
SRC=(ROOT/'MainWindow.xaml.cs').read_text()
def method(name):
    m=re.search(r'        private [^\n]+ '+name+r'\([^\n]*\)\s*\{',SRC)
    assert m,name
    i,depth=m.end(),1
    while depth:
        depth+=(SRC[i]=='{')-(SRC[i]=='}');i+=1
    return SRC[m.start():i]
class OcrScopeTests(unittest.TestCase):
    def test_actual_distribution(self):
        code='''using System.Windows; using System; using System.Linq; using System.Collections.Generic; using System.Text.RegularExpressions;
class Item {public string Name="", DisplayLabel=""; public object? Value;}
class Section {public List<Item> Items=new();}
class Inspection {public List<Section> Sections=new();}
static class EditorEditService {public static bool Owns(Inspection? i,Item x)=>i?.Sections.Any(s=>s.Items.Contains(x))==true;}
namespace System.Windows { public struct Thickness {public Thickness(int a,int b,int c,int d){}} namespace Media {public static class Brushes {public static object Green=new();}} namespace Controls {public class TextBlock {public string Text="";public object? Foreground,FontStyle,Margin,TextWrapping;public int FontSize;}}}
static class FontStyles {public static object Italic=new();} static class TextWrapping {public static object Wrap=new();}
class Stack {public List<object> Children=new();}
class Probe {
 Inspection? _currentInspection=new();bool _readOnlyMode; Stack SuggestionsStack=new(); void MarkUnsaved(){}
 static int checks;static void Check(bool b,string n){if(!b)throw new Exception(n);checks++;}
 static Item I(string n,string? v=null)=>new(){Name=n,Value=v};
 public static void Main(){
  var p=new Probe();var m=I("Unit: Make/Model");var s=I("Unit: Serial Number");var m2=I("Unit: Make/Model (unit 2)");var s2=I("Unit: Serial Number (unit2)");
  p._currentInspection!.Sections.Add(new(){Items=new(){m,s,m2,s2}});
  p.ApplyTranscriptionSuggestion(m,"MODEL NO: CHPEA3626B3AA / SERIAL NO: 2604250521 / VOLTS: 24 / HERTZ: 60");
  Check((string?)m.Value=="CHPEA3626B3AA","selected model");Check((string?)s.Value=="2604250521","sameunit serial");Check(m2.Value==null&&s2.Value==null,"no phantomunit2");
  s.Value="existing";p.ApplyTranscriptionSuggestion(m,"Model: NEW / Serial: WRONG");Check((string?)s.Value=="existing","partner existing preserved");
  s.Value="NI";p.ApplyTranscriptionSuggestion(m,"Model: NEW / Serial: WRONG");Check((string?)s.Value=="NI","NI preserved");
  p.ApplyTranscriptionSuggestion(m2,"Model: SECOND / Serial: SECOND-SERIAL");Check((string?)m2.Value=="SECOND"&&(string?)s2.Value=="SECOND-SERIAL","explicitunit2 siblings");Check((string?)s.Value=="NI","unit1 untouched by2");
  var cross=I("Unit: Serial Number");p._currentInspection.Sections=new(){new(){Items=new(){m}},new(){Items=new(){cross}}};p.ApplyTranscriptionSuggestion(m,"Model: M / Serial: S");Check(cross.Value==null,"section boundary");
  var u=I("U-Factor");var sh=I("SHGC");p._currentInspection.Sections=new(){new(){Items=new(){u,sh}}};p.ApplyTranscriptionSuggestion(u,"U-Factor: 0.30 / SHGC: 0.25");Check((string?)u.Value=="0.30"&&(string?)sh.Value=="0.25","window companion preserved");
  sh.Value=null;p.ApplyTranscriptionSuggestion(u,"0.32");Check(sh.Value==null&&(string?)u.Value=="0.32","selected simple option only");
  var stale=I("Model");p.ApplyTranscriptionSuggestion(stale,"MODEL: X / SERIAL: Y");Check(stale.Value==null,"stale owner rejected");
  p._readOnlyMode=true;p.ApplyTranscriptionSuggestion(u,"9");Check((string?)u.Value=="0.32","readonly rejected");
  Check(!TranscriptionKeyMatchesItem("Unit 2 Model",m),"explicit OCRunit mismatch");
  Console.WriteLine($"PASS {checks} actual OCR distribution checks");
 }
'''
        code+='\n'.join(method(n) for n in ['ApplyTranscriptionSuggestion','TranscriptionUnit','ParseTranscriptionPairs','TranscriptionKeyMatchesItem'])+'\n}'
        with tempfile.TemporaryDirectory() as t:
            d=Path(t);(d/'Program.cs').write_text(code)
            (d/'Probe.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework><RollForward>Major</RollForward><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>')
            r=subprocess.run(['dotnet','run','--project',str(d/'Probe.csproj')],capture_output=True,text=True)
            self.assertEqual(r.returncode,0,r.stdout+r.stderr);print(r.stdout.strip())
if __name__=='__main__':unittest.main()
