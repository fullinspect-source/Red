#!/usr/bin/env python3
"""Execute production model, target snapshot service and EC mapping/application methods.
Native PDF/OCR and WPF are deliberately not part of this portable harness.
"""
from pathlib import Path
import subprocess
import tempfile
import shutil
ROOT = Path(__file__).resolve().parents[1]
s = (ROOT / 'Services/EnergyComplianceService.cs').read_text()
def method(start, end):
    return s[s.index(start):s.index(end)]
core = method('    public class EnergyComplianceInfo', '        public static EnergyComplianceInfo GetInfoForInspection')
helpers = method('        private static bool SetItemValue', '        private static string? First(')
# Production models use Newtonsoft. Same package as the application, no schema stubs.
program = r'''
using InspectionEditor.Models;
using InspectionEditor.Services;
using Newtonsoft.Json;
class Program {
 static int checks;
 static void Eq(object? got, object? expected, string name) { checks++; if (!Equals(got,expected)) throw new Exception($"{name}: [{got}] != [{expected}]"); }
 static void Main(string[] args) {
 var inspection = JsonConvert.DeserializeObject<InspectionFile>(File.ReadAllText(args[0]))!;
 var before = JsonConvert.SerializeObject(inspection);
 var info = new EnergyComplianceInfo { StatusText = "No EC report found for this job." };
 TestingTargetsService.Refresh(info, inspection);
 Eq(info.TestingTargets.Count,6,"real populated fields");
 foreach (var pair in new Dictionary<string,string> { ["DUCTLEAKAGEMAXCFM"]="46", ["BLOWERDOORMAXCFM"]="694", ["CONDITIONEDFLOORAREA"]="1,156", ["CONDITIONEDVOLUME"]="9,248", ["TARGETFRESHAIRCFM"]="145.00", ["TARGETRUNTIME"]="6.90" }) Eq(info.TestingTargets[pair.Key], pair.Value, pair.Key);
 Eq(info.IsLoaded,false,"not EC loaded"); Eq(info.HasAvailableTargets,true,"targets available");
 Eq(info.ExtractedFieldCount,0,"no false OCR count");
 Eq(info.EffectiveBlowerDoorCfm,null,"no ACH guess"); Eq(info.EffectiveDuctLeakageCfm,null,"no area guess");
 foreach (var p in new Dictionary<string,string> { ["2.12"]="694", ["3.3"]="46", ["5.5"]="145.00", ["5.7"]="6.90" }) {
 Eq(EnergyComplianceService.GetValueForItem(info,"HET",p.Key),p.Value,p.Key);
 Eq(EnergyComplianceService.CanApplyToItem("HET",p.Key),false,"measurement guidance only");
 Eq(EnergyComplianceService.GetTargetSourceForItem(info,"HET",p.Key)!.Contains("Testing Targets"),true,"source");
 var item = new Item { Number=p.Key,ControlName="Text",Value="actual" };
 Eq(EnergyComplianceService.ApplySingleItem(info,item,"HET"),false,"cannot apply measurement"); Eq(item.Value,"actual","preserve measured"); }
 foreach (var n in new [] { "1.1","1.2","1.3","1.4","1.5","1.6","1.7","3.4","3.5","3.6","3.1","2.17","3.7" }) Eq(EnergyComplianceService.GetValueForItem(info,"HET",n),null,"no self/unit2/outside/guessed/status: " + n);
 Eq(EnergyComplianceService.GetValueForItem(info,"IER","2.1"),null,"no performance default");
 Eq(JsonConvert.SerializeObject(inspection),before,"snapshot never mutates inspection");
 info.BlowerDoorMaxCfm="700"; Eq(EnergyComplianceService.GetValueForItem(info,"HET","2.12"),"700","EC primary"); Eq(EnergyComplianceService.GetTargetSourceForItem(info,"HET","2.12"),null,"EC provenance");
 Eq(EnergyComplianceService.GetValueForItem(info,"HET","3.3"),"46","partial EC fallback");
 foreach(var control in new [] {"PassFail","PassFailNaNi","YesNo","YesNoNaNi"}) { var status = new Item {Number="1.3",ControlName=control,Value="Fail"}; Eq(EnergyComplianceService.ApplySingleItem(info,status,"HET"),false,"no status apply"); Eq(status.Value,"Fail","status retained"); }
 info.StatusText="Error reading EC report"; Eq(EnergyComplianceService.GetValueForItem(info,"HET","5.5"),"145.00","error fallback");
 inspection.Sections[0].Items.Single(x=>x.Number=="1.1").Value="55";
 TestingTargetsService.Refresh(info,inspection); Eq(EnergyComplianceService.GetValueForItem(info,"HET","3.3"),"55","edit refresh");
 inspection.Sections[0].Items.Single(x=>x.Number=="1.1").Value="";
 TestingTargetsService.Refresh(info,inspection); Eq(EnergyComplianceService.GetValueForItem(info,"HET","3.3"),null,"clear removes target");
 var unit2=inspection.Sections[0].Items.Single(x=>x.Number=="1.2"); unit2.Value="23";
 TestingTargetsService.Refresh(info,inspection); Eq(EnergyComplianceService.GetValueForItem(info,"HET","3.4"),"23","explicit unit2"); Eq(EnergyComplianceService.GetValueForItem(info,"HET","3.6"),null,"not outside");
 foreach(var bad in new [] {"NI","N/A","NaN","-2","0","1,2","20 CFM","1e3"}) {unit2.Value=bad;TestingTargetsService.Refresh(info,inspection); Eq(TestingTargetsService.Value(info,"DUCTLEAKAGEMAXCFM2"),null,"reject "+bad);}
 inspection.Sections[0].Items.Add(inspection.Sections[0].Items.Single(x=>x.Number=="1.3")); TestingTargetsService.Refresh(info,inspection); Eq(TestingTargetsService.Value(info,"BLOWERDOORMAXCFM"),null,"ambiguous duplicates");
 inspection.InspectionCode="IEF"; TestingTargetsService.Refresh(info,inspection); Eq(info.TestingTargets.Count,0,"inspection switch clears");
 TestingTargetsService.Refresh(info,null); Eq(info.TestingTargets.Count,0,"null inspection");
 Console.WriteLine($"PASS {checks} production Testing Targets checks");
 }
}
'''
with tempfile.TemporaryDirectory(prefix='red-targets-') as d:
    d = Path(d)
    (d/'Harness.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings></PropertyGroup><ItemGroup><PackageReference Include="Newtonsoft.Json" Version="13.0.3" /></ItemGroup></Project>')
    (d/'Ec.cs').write_text('using InspectionEditor.Models; using System.Text.RegularExpressions; namespace InspectionEditor.Services {\n'+core+helpers+'\n}\n}\n')
    shutil.copy(ROOT/'Models/InspectionModels.cs',d/'Models.cs')
    shutil.copy(ROOT/'Services/TestingTargetsService.cs',d/'Targets.cs')
    (d/'Mapping.cs').write_text('namespace InspectionEditor.Services { public static class ExtractionMappingService { public static string NormalizeFieldKey(string key) => System.Text.RegularExpressions.Regex.Replace(key.ToUpperInvariant(), "[^A-Z0-9]", ""); } }')
    (d/'Program.cs').write_text(program)
    subprocess.run(['dotnet','run','--project',str(d/'Harness.csproj'),'--',str(ROOT/'tests/fixtures/testing-targets-real-redacted.json')],check=True)
# UI wiring assertions supplement execution; they do not claim native WPF validation.
u = (ROOT/'MainWindow.xaml.cs').read_text()
assert 'capturedEcRequestId != _ecExtractionRequestId' in u
assert '!ReferenceEquals(capturedInspection, _currentInspection)' in u
assert 'TestingTargetsService.Refresh(info, _currentInspection);' in u
assert 'Target only; enter the actual field measurement.' in u
print('PASS UI generation/identity/latest-snapshot/guidance wiring checks')
