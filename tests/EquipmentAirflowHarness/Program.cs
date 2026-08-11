using InspectionEditor.Models;
using InspectionEditor.Services;
using Newtonsoft.Json;

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static Item Model(string number, string name, string value) =>
    new() { Number = number, Name = name, Value = value };

static InspectionFile EnergyFinal(string jobId, string outdoor1, string indoor1,
    string? outdoor2 = null, string? indoor2 = null)
{
    return new InspectionFile
    {
        InspectionNumber = $"{jobId}-IEF-1-TF",
        InspectionCode = "IEF",
        Sections = new()
        {
            new Section
            {
                Name = "Condenser Unit",
                Items = new()
                {
                    Model("5.2", "Unit: Model Number (unit 1)", outdoor1),
                    Model("5.5", "Unit: Make/Model (unit 2)", outdoor2 ?? "")
                }
            },
            new Section
            {
                Name = "Evaporator Coil/Air Handler Unit",
                Items = new()
                {
                    Model("6.3", "Unit: Model Number (unit 1)", indoor1),
                    Model("6.6", "Unit: Model Number (unit 2)", indoor2 ?? "")
                }
            }
        }
    };
}

Assert(EquipmentAirflowService.GetAirflowForModels("GZV6SA24-ABC", "AHVE24BP13 AA") == 773,
    "suffix/format normalization failed");
Assert(EquipmentAirflowService.GetAirflowForModels("GZV6SA30", "AHVE36CP13") == 947,
    "947 CFM rule failed");
Assert(EquipmentAirflowService.GetAirflowForModels("GZV6SA36", "AHVE36CP13") == 1140,
    "1140 CFM rule failed");
Assert(EquipmentAirflowService.GetAirflowForModels("GLZS4BA30", "AMST42CU13") == 953,
    "953 CFM rule failed");
Assert(EquipmentAirflowService.GetAirflowForModels("GZV6SA42", "AHVE42CP13") == 1367,
    "1367 CFM rule failed");
Assert(EquipmentAirflowService.GetAirflowForModels("GZV6SA24", "wrong") == null,
    "partial matchup must not produce a target");

string root = Path.Combine(Path.GetTempPath(), "red-airflow-harness-" + Guid.NewGuid().ToString("N"));
string review = Path.Combine(root, "Review");
Directory.CreateDirectory(review);
try
{
    string jobId = "2999999";
    string afiPath = Path.Combine(review, $"{jobId}-AFI-1-TF.ins");
    string iefPath = Path.Combine(review, $"{jobId}-IEF-1-TF.ins");
    File.WriteAllText(afiPath, "{}");
    File.WriteAllText(iefPath, JsonConvert.SerializeObject(EnergyFinal(
        jobId, "GZV6SA24", "AHVE24BP13", "GZV6SA42", "AHVE42CP13")));

    var matches = EquipmentAirflowService.FindMatches(afiPath, null);
    Assert(matches.Count == 2, "same-job Energy Final lookup did not find both units");
    Assert(matches[0].AirflowCfm == 773 && matches[1].AirflowCfm == 1367,
        "unit-specific airflow targets were not retained");

    var info = new EnergyComplianceInfo
    {
        DesignAirflowCfm = "1080",
        StatusText = "Original EC status",
        DisplayName = "Original EC.pdf"
    };
    EquipmentAirflowService.ApplyMatches(info, afiPath, null);
    Assert(info.DesignAirflowCfm == "773", "equipment matchup did not override tonnage rule");
    Assert(info.DesignAirflowCfm2 == "1367", "unit 2 target missing");
    Assert(info.DesignAirflowSource == "STRADA equipment matchup", "source label missing");

    File.Delete(iefPath);
    EquipmentAirflowService.ApplyMatches(info, afiPath, null);
    Assert(info.DesignAirflowCfm == "1080", "fallback target was not restored after matchup removal");
    Assert(info.DesignAirflowCfm2 == null && info.DesignAirflowSource == null,
        "stale unit/source data remained after matchup removal");
    Assert(info.StatusText == "Original EC status" && info.DisplayName == "Original EC.pdf",
        "original EC status metadata was not restored");

    string otherJob = "2888888";
    string otherAfi = Path.Combine(review, $"{otherJob}-AFI-1-TF.ins");
    File.WriteAllText(otherAfi, "{}");
    File.WriteAllText(Path.Combine(review, $"{otherJob}-IEF-1-TF.ins"),
        JsonConvert.SerializeObject(EnergyFinal(otherJob, "GZV6SA24", "AHVE24BP13")));
    File.WriteAllText(Path.Combine(review, $"{otherJob}-IEF-2-TF.ins"),
        JsonConvert.SerializeObject(EnergyFinal(otherJob, "UNKNOWN", "UNKNOWN")));
    Assert(EquipmentAirflowService.FindMatches(otherAfi, null).Count == 0,
        "an older attempt must not override the newest Energy Final");

    var inMemory = EnergyFinal(jobId, "GLZS4BA30", "AMST42CU13");
    var liveMatches = EquipmentAirflowService.FindMatches(iefPath, inMemory);
    Assert(liveMatches.Count == 1 && liveMatches[0].AirflowCfm == 953,
        "unsaved in-memory Energy Final values were not recognized");
}
finally
{
    Directory.Delete(root, recursive: true);
}

Console.WriteLine("Equipment airflow harness: PASS");
