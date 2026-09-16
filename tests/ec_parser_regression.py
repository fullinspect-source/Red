#!/usr/bin/env python3
"""Run actual production ParseText/model/helpers in a disposable .NET console project.
No WPF/PDF/OCR substitutes: only source extraction and a public invocation wrapper.
"""
from pathlib import Path
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[1]
source = (ROOT / 'Services/EnergyComplianceService.cs').read_text()
model = source[source.index('    public class EnergyComplianceInfo'):source.index('    public static class EnergyComplianceService')]
parser = source[source.index('        private static void ParseText('):source.index('        // Value application helpers')]
helpers = source[source.index('        private static string? First('):source.index('        public static string? GetTessDataPathPublic')]
tests = r'''
class Program {
 static int checks;
 static EnergyComplianceInfo Parse(string text) { var i = new EnergyComplianceInfo(); EnergyComplianceService.Run(text, i); return i; }
 static void Eq(object? actual, object? expected, string name) { checks++; if (!Equals(actual, expected)) throw new Exception($"{name}: expected [{expected}], actual [{actual}]"); }
 static void Main(string[] args) {
 var i = Parse(File.ReadAllText(args[0]));
 Eq(i.HersIndex,"50","real HERS"); Eq(i.ConditionedFloorArea,"1,156","real area");
 Eq(i.ConditionedVolume,"9,248","real volume"); Eq(i.NumberOfBedrooms,"3","real bedrooms");
 Eq(i.BlowerDoorMaxCfm,"694","real blower CFM"); Eq(i.BlowerDoorSourceAch,"4.5","real ACH provenance");
 Eq(i.DuctLeakageMaxCfm,"46","real duct leakage"); Eq(i.NumberOfReturns,"2","real returns");
 Eq(i.SupplyDuctR,"R6","real supply R"); Eq(i.ReturnDuctR,"R6","real return R");
 Eq(i.WindowUFactor,"0.34","real U"); Eq(i.WindowSHGC,"0.22","real SHGC");
 Eq(i.WallR,"R15","real wall"); Eq(i.AtticCeilingR,"R38","real attic");
 Eq(i.AtticWallR,null,"no attic wall invention"); Eq(i.AtticRoofR,null,"no roof invention"); Eq(i.SlopedCeilingR,null,"no slope invention");
 Eq(i.WaterHeaterCapacity,"Tankless","real tankless"); Eq(i.WaterHeaterFuel,"Gas","real WH fuel"); Eq(i.HotWaterPipeR,"R3","real pipe");
 Eq(i.HvacCoolingSeer,"17.2 SEER2","real SEER"); Eq(i.HvacCoolingCapacityKbtu,"24.4","real capacity"); Eq(i.HvacTonnage,"2","real nominal tons");
 Eq(i.DesignAirflowCfm,"720","real explicit airflow"); Eq(i.DesignAirflowSource,"EC Cooling Flow Rate (explicit CFM)","real flow provenance");
 Eq(i.TargetFreshAirCfm,"145","real ventilation"); Eq(i.TargetRunTime,"6.9 hrs/day (29%, 17.3 min/hr)","real runtime"); Eq(i.VentFanWatts,"38","real watts");
 Eq(i.IsLoaded,true,"real loaded"); Console.WriteLine("PASS captured real OCR: " + i.ExtractionStatus);
 i=Parse("HERS° Index Score:50\nConditioned Floor Area [sq. ft.|: 1,156\nConditioned Volume [cu. ft|: 9,248\nSHGC: 0.22");
 Eq(i.HersIndex,"50","degree glyph"); Eq(i.ConditionedFloorArea,"1,156","malformed area unit"); Eq(i.ConditionedVolume,"9,248","malformed volume unit"); Eq(i.WindowSHGC,"0.22","colon SHGC");
 i=Parse("HERS Index Score: 60\nConditioned Floor Area [sq. ft.]: 2,218\nConditioned Volume [cu. ft.]:\n17,744\nNumber of Bedrooms: 4\nHouse Tightness: 850 CFM50\nTotal Duct Leakage: 70 CFM\n# Return Grilles\n3\nSupply Duct R Value\n8\nReturn Duct R Value\n6\nCooling Efficiency\n15.2 SEER2\nCooling Capacity [kBtu/h]\n34");
 Eq(i.ConditionedFloorArea,"2,218","old area"); Eq(i.ConditionedVolume,"17,744","old volume"); Eq(i.BlowerDoorMaxCfm,"850","old blower"); Eq(i.NumberOfReturns,"3","old returns"); Eq(i.SupplyDuctR,"R8","old supply"); Eq(i.ReturnDuctR,"R6","old return"); Eq(i.HvacTonnage,"3","old capacity"); Eq(i.DesignAirflowCfm,"1080","old estimate");
 i=Parse("17.2 SEER2 A/C (24.4K)\nEquipment Type: 17.2 SEER2 A/C (24.4K)\nCooling Capacity [kBtu/h] 24.4\nCooling Capacity [kBtu/h] 24.4\nCooling Flow Rate Cfm 777");
 Eq(i.HvacCoolingCapacityKbtu,null,"ambiguous duplicate capacity"); Eq(i.HvacTonnage,null,"ambiguous duplicate tons"); Eq(i.DesignAirflowCfm,"777","explicit overrides estimate");
 i=Parse("15.2 SEER2 A/C (34K)\n15.2 SEER2 A/C (34K)"); Eq(i.HvacTonnage,null,"ambiguous duplicate library only");
 i=Parse("Conditioned Volume: N/A Bedrooms 3\nSupply Duct R Value: N/A Heating Capacity 42\nReturn Duct R Value: N/A Cooling Capacity unknown 24\nCooling Flow Rate Cfm N/A Heating Flow Rate Cfm 900\n17\nConditioned Floor Area: 1,200");
 Eq(i.ConditionedVolume,null,"no adjacent volume"); Eq(i.SupplyDuctR,null,"no adjacent supply"); Eq(i.ReturnDuctR,null,"no adjacent return"); Eq(i.HvacTonnage,null,"no adjacent capacity"); Eq(i.DesignAirflowCfm,null,"no adjacent flow"); Eq(i.NumberOfReturns,null,"no adjacent returns");
 i=Parse("U-Value: 0.34, SHGC: 0.22"); Eq(i.ExtractedFieldCount,2,"summary-only count"); Eq(i.IsLoaded,false,"partial does not broaden application gate"); Eq(i.ExtractionStatus.Contains("2 fields available"),true,"partial truthful status");
 i=Parse("ENERGY STAR v3.1 IECC 2021 Climate Zone: 2A"); Eq(i.ExtractedFieldCount,0,"program only no field data"); Eq(i.IsLoaded,false,"program only cannot enable apply");
 i=Parse("Roof Deck R22"); Eq(i.SlopedCeilingR,null,"roof does not imply sloped");
 i=Parse("Cooling Capacity [kBtu/h] 24.4\nCooling Flow Rate Cfm 720\nCooling Capacity [kBtu/h] 24.4\nCooling Flow Rate Cfm 800");
 Eq(i.HvacTonnage,null,"equal physical units ambiguous tonnage"); Eq(i.DesignAirflowCfm,"720","unit1 explicit"); Eq(i.DesignAirflowCfm2,"800","unit2 explicit");
 Console.WriteLine($"PASS {checks} parser regression checks");
 }
}
'''
with tempfile.TemporaryDirectory(prefix='red-ec-parser-') as tmp:
    tmp = Path(tmp)
    (tmp / 'Harness.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>')
    (tmp / 'Program.cs').write_text('using System.Text.RegularExpressions;\n' + model + '\npublic static class EnergyComplianceService {\npublic static void Run(string text, EnergyComplianceInfo i) => ParseText(text, i);\n' + parser + helpers + '\n}\n' + tests)
    subprocess.run(['dotnet', 'run', '--project', str(tmp / 'Harness.csproj'), '--', str(ROOT / 'tests/fixtures/ec-real-redacted.txt')], check=True)
