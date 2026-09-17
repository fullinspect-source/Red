"""Global wiring and production custom resolver regression checks."""
from pathlib import Path
import subprocess,tempfile,unittest
ROOT=Path(__file__).resolve().parents[1]
class MappingSafety(unittest.TestCase):
 def test_all_ec_ui_uses_semantic_resolution(self):
  s=(ROOT/'MainWindow.xaml.cs').read_text()
  self.assertNotIn('EnergyComplianceService.GetValueForItem(',s)
  self.assertNotIn('EnergyComplianceService.GetLabelForItem(',s)
  self.assertIn('CanApply: resolved.CanApply',s)
  self.assertIn('fresh == null || !fresh.CanApply || fresh.Value != assist.Value',s)
  self.assertIn('Unit 1 Source:',s);self.assertIn('Unit 2 Source:',s)
 def test_production_custom_resolver(self):
  code='''using System;using System.Collections.Generic;using System.Reflection;using InspectionEditor.Services;
namespace InspectionEditor.Services {static class EnergyComplianceService {public static string NormalizeCode(string? s)=>(s??"").ToUpperInvariant();}}
class Probe {static int checks; static void C(bool b){if(!b)throw new Exception("mapping check "+checks);checks++;}
static void Set(params ExtractionMapping[] rows)=>typeof(ExtractionMappingService).GetField("_mappings",BindingFlags.NonPublic|BindingFlags.Static)!.SetValue(null,new List<ExtractionMapping>(rows));
static ExtractionMapping R(string p,string f,string num="")=>new(){Source="EC",InspectionCode="IEF",PromptMatch=p,FieldKey=f,LegacyItemNumber=num};
static ExtractionMapping? Get(string p,string n="3.4")=>ExtractionMappingService.Resolve("EC","IEF","3","Condenser Unit",p,n);
static void Main(){
 Set(R("","WaterHeaterFuel","3.4"));C(Get("Unit 2 Serial Number")==null);
 Set(R("water heater fuel","WaterHeaterFuel"));C(Get("fuel")==null);C(Get("water heater fuel")?.FieldKey=="WaterHeaterFuel");C(Get("water heater fuelish")==null);
 Set(R("design airflow","DesignAirflowCfm"),R("tonnage","HvacTonnage"));C(Get("Design airflow - tonnage x 360CFM")==null);
 Set(R("serial number","DesignAirflowCfm"),R("serial number","WaterHeaterFuel"));C(Get("Unit 2 Serial Number")==null);
 Set(R("model","A"));C(Get("remodel")==null);
 Console.WriteLine($"PASS {checks} production custom resolver checks");}}
'''
  with tempfile.TemporaryDirectory() as t:
   d=Path(t);(d/'Program.cs').write_text(code)
   (d/'Mapping.cs').write_text((ROOT/'Services/ExtractionMappingService.cs').read_text())
   (d/'Probe.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net8.0</TargetFramework><RollForward>Major</RollForward><Nullable>enable</Nullable></PropertyGroup></Project>')
   r=subprocess.run(['dotnet','run','--project',str(d/'Probe.csproj')],capture_output=True,text=True)
   self.assertEqual(r.returncode,0,r.stdout+r.stderr);print(r.stdout.strip())
if __name__=='__main__':unittest.main()
