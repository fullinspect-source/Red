using InspectionEditor.Models;
using InspectionEditor.Services;
using Newtonsoft.Json;
int tests=0;
void Check(bool condition,string message) { tests++; if(!condition) throw new Exception(message); }
var info=new EnergyComplianceInfo { HersIndex="50", WaterHeaterFuel="Gas", WaterHeaterCapacity="50 Gallon", HvacCoolingSeer="15 SEER2", DesignAirflowCfm="1080", DesignAirflowCfm2="720", ConditionedFloorArea="2000",ConditionedVolume="16000",BlowerDoorMaxCfm="800",DuctLeakageMaxCfm="80",TargetFreshAirCfm="60",TargetRunTime="8",NumberOfReturns="3",AtticCeilingR="R30",HotWaterPipeR="R3" };
var templates=JsonConvert.DeserializeObject<List<InspectionFile>>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"templates.json")))!;
int resolvedCount=0;
foreach(var template in templates) {
 foreach(var section in template.Sections) foreach(var item in section.Items) {
  var r=EnergySemanticMappingService.Resolve(info,template.InspectionCode,section,item);
  if(r!=null) resolvedCount++;
  if((item.Name??"").Contains("Serial",StringComparison.OrdinalIgnoreCase)||(item.Name??"").Contains("model",StringComparison.OrdinalIgnoreCase)) {
   Check(r==null||!r.CanApply,"ID writable "+item.Name);
   Check(!EnergyComplianceService.ApplySingleItem(info,item,template.InspectionCode,section),"ID mutated");
  }
  if((item.ControlName??"").StartsWith("Pass")||(item.ControlName??"").StartsWith("Yes")) Check(r==null,"Boolean resolved");
  if(r!=null) {
   var before=r; var old=item.Number;item.Number="999.999";
   Check(EnergySemanticMappingService.Resolve(info,template.InspectionCode,section,item)==before,"position dependency");item.Number=old;
   Check(EnergySemanticMappingService.Resolve(info,template.InspectionCode,section,item,"WRONGFIELD")==null,"custom mismatch accepted");
  }
 }
 Check(EnergyComplianceService.ApplyToInspection(info,template)>0,"all useful mappings disabled "+template.InspectionCode);
}
Item Row(string name,string number="3.4",string ctrl="Text")=>new(){Name=name,Number=number,ControlName=ctrl};
var condenser=new Section{Name="Condenser Unit"};var wh=new Section{Name="Plumbing Hot Water Service"};
Check(EnergySemanticMappingService.Resolve(info,"IEF",condenser,Row("Unit: Serial Number (unit 2)")) is {Value:"720",CanApply:false,Units:"CFM"},"condenser unit2");
Check(EnergySemanticMappingService.Resolve(info,"IEF",wh,Row("Model","4.2"))==null,"WH model airflow");
Check(EnergySemanticMappingService.Resolve(info,"IEF",new Section{Name="HVAC Systems"},Row("HVAC SEER","5.2")) is {Value:"15 SEER2",CanApply:true},"SEER actual slot");
Check(EnergySemanticMappingService.Resolve(info,"IEF",null,Row("Mechanical ventilation system type","5.3"))==null,"vent type CFM");
Check(EnergySemanticMappingService.Resolve(info,"IEF",null,Row("Capacity"))==null,"ambiguous capacity");
Check(EnergySemanticMappingService.Resolve(info,"IEF",null,Row("SEER model"))==null,"ambiguous model");
Check(EnergySemanticMappingService.Resolve(info,"HET",null,Row("Duct system #1 leakage to outside CFM"))==null,"total target outside");
info.DesignAirflowCfm2=null;
Check(EnergySemanticMappingService.Resolve(info,"IEF",condenser,Row("Unit: Serial Number (unit 2)"))==null,"unit2 falls back unit1");
var actual=Row("Blower door test #1 (50 Pa) CFM","2.12");info.BlowerDoorMaxCfm=null;
info.TestingTargets["BLOWERDOORMAXCFM"]="777";info.TestingTargetSources["BLOWERDOORMAXCFM"]="Section 1.3 Testing Targets (current inspection)";
Check(EnergySemanticMappingService.Resolve(info,"HET",null,actual) is {Value:"777",CanApply:false,Source:"Section 1.3 Testing Targets (current inspection)"},"fallback provenance");
Check(!EnergyComplianceService.ApplySingleItem(info,actual,"HET"),"actual written");
var pipe=Row("Hot water pipe R-value",ctrl:"Lookup");pipe.ValueList=new(){"R30"};
Check(!EnergyComplianceService.ApplySingleItem(info,pipe,"IER"),"R3 selected R30");pipe.ValueList=new(){"R-3"};
Check(EnergyComplianceService.ApplySingleItem(info,pipe,"IER"),"R-3 equivalent lost");
var seer=Row("HVAC SEER",ctrl:"Lookup");seer.ValueList=new(){"15 SEER"};
Check(!EnergyComplianceService.ApplySingleItem(info,seer,"IEF"),"SEER2 became SEER");
var partial = new EnergyComplianceInfo { WaterHeaterFuel = "Gas" };
Check(partial.HasAvailableTargets, "partial gas availability");
Check(EnergySemanticMappingService.Resolve(partial,"IEF",wh,Row("Gas/Electric")) is {Value:"Gas",CanApply:true}, "partial gas resolved");
Check(new EnergyComplianceInfo { WindowUFactor="0.30" }.HasAvailableTargets,"partial window available");
Check(EnergySemanticMappingService.Resolve(info,"IEF",condenser,Row("Unit: Serial Number (unit 3)"))==null,"unknown unit rejected");
Check(EnergySemanticMappingService.Resolve(info,"IEF",new Section{Name="High Performance Fenestration"},Row("Type"))==null,"no fabricated compliance path");
Check(!EnergySemanticMappingService.IsDesignMismatch(new("WINDOWUFACTOR", "0.30", "U", "", "EC report", true), ".3"), "leading decimal alias");
// The linked production resolver and comparison method drive these regressions.
var designPipe = Row("Hot water piping insulation R-value", "9.1", "LookupNANI");
designPipe.Value = "NI";
var pipeDesign = EnergySemanticMappingService.Resolve(info, "IER", null, designPipe);
Check(EnergySemanticMappingService.IsDesignMismatch(pipeDesign, "NI"), "NI vs concrete R3 must be pink");
Check(EnergySemanticMappingService.IsDesignMismatch(pipeDesign, "R2"), "R2 vs R3 must be pink");
foreach (var equal in new[] { "R3", "R-3", " r 3 ", "R3.0", "3" })
 Check(!EnergySemanticMappingService.IsDesignMismatch(pipeDesign, equal), "equivalent R value: " + equal);
Check(!EnergySemanticMappingService.IsDesignMismatch(null, "NI"), "no reference stays normal");
Check(!EnergySemanticMappingService.IsDesignMismatch(pipeDesign, ""), "blank stays normal");
Check(!EnergySemanticMappingService.IsDesignMismatch(EnergySemanticMappingService.Resolve(new EnergyComplianceInfo(), "IER", null, designPipe), "NI"), "missing extraction stays normal");
Check(!EnergySemanticMappingService.IsDesignMismatch(EnergySemanticMappingService.Resolve(info, "IER", null, Row("Unrelated field", "9.1")), "NI"), "position cannot imply mapping");
Check(!EnergySemanticMappingService.IsDesignMismatch(EnergySemanticMappingService.Resolve(info, "HET", null, actual), "500"), "measured value below max is not equality mismatch");
Check(designPipe.Value?.ToString() == "NI", "comparison never changes answer");
Check(!EnergySemanticMappingService.IsDesignMismatch(pipeDesign! with { FieldKey="CONDITIONEDFLOORAREA", Units="ft²", Value="2,316" }, "2316"), "numeric grouping");
Check(!EnergySemanticMappingService.IsDesignMismatch(pipeDesign! with { FieldKey="HVACCOOLINGSEER", Units="", Value="15 SEER2" }, "15.0 SEER2"), "numeric SEER precision");
Check(EnergySemanticMappingService.IsDesignMismatch(pipeDesign! with { FieldKey="HVACCOOLINGSEER", Units="", Value="15 SEER2" }, "15 SEER"), "units must remain distinct");
Check(!EnergySemanticMappingService.IsDesignMismatch(pipeDesign! with { Value="N/A" }, "NI"), "placeholder is not concrete design");
Console.WriteLine($"PASS {tests} assertions; {templates.Count} real templates; {resolvedCount} resolved rows; codes {string.Join(',',templates.Select(t=>t.InspectionCode).Distinct())}");
