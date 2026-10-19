// F-026 inspection history harness (program/features/F-026-history-events/ACCEPTANCE.md).
//   dotnet run -p:Platform=x64
// Synthetic fixture in a sandbox Inspections folder under %TEMP%; nothing is sent anywhere (an Inspector submit is
// local file work, and the report preview renders locally).
using System.IO;
using System.Text;
using InspectionEditor.Services;
using InspectionEditor.Services.Reports;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

return await Harness.Run();

static class Harness
{
    private static int _pass, _fail;
    private const string Username = "inspector@example.com";

    private static void Check(string id, bool ok, string what)
    {
        if (ok) _pass++; else _fail++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {id}  {what}");
    }

    private sealed class NoLog : ISubmitLog
    {
        public void Inspect(string line) { }
        public void Red(string line) { }
    }

    private static JObject Item(int id, string number, string name, string control, string? value, bool required = false) => new()
    {
        ["ItemId"] = id, ["Name"] = name, ["Number"] = number, ["ControlName"] = control, ["ItemGroupId"] = "", ["PictureSizeId"] = 0,
        ["IsCopied"] = false, ["Hide"] = false, ["Value"] = value, ["Comments"] = "", ["Pictures"] = new JArray(), ["Required"] = required,
        ["AlwaysPrint"] = false, ["OnlyPrint"] = "", ["IsPictureRequired"] = false
    };

    private static readonly JArray InputEvents = new(
        new JObject { ["Date"] = "2026-10-01T08:00:00-05:00", ["EventTypeId"] = 30, ["Username"] = Username, ["UserDisplayName"] = "Pat Q Example", ["AdditionalInfo"] = null },
        new JObject { ["Date"] = "2026-10-17T09:00:00-05:00", ["EventTypeId"] = 40, ["Username"] = Username, ["UserDisplayName"] = "Pat Q Example", ["AdditionalInfo"] = "Inspect Version 2026.1012.2" });

    private static JObject Fixture() => new()
    {
        ["InspectVersion"] = null, ["WorkOrderId"] = "00000000-0000-0000-0000-000000000026", ["ServiceId"] = 1, ["FormId"] = 20,
        ["IsTransfer"] = true, ["IsPreinspection"] = false, ["NextActionId"] = 0, ["NextActionText"] = null, ["InspectionName"] = "Concrete Pre Pour",
        ["InspectionNumber"] = "1234567-CPP-1-PQE", ["InspectionCode"] = "CPP", ["Attempt"] = 1, ["StatusId"] = 2,
        ["StatusList"] = new JArray(new JObject { ["Id"] = 2, ["Name"] = "Passed" }), ["BuilderName"] = "Example Builder", ["JobNumber"] = "1234567",
        ["Address"] = "100 Example St", ["City"] = "Example City", ["InspectorUsername"] = Username, ["InspectorName"] = "Pat Q Example",
        ["InspectorPhone"] = "000-000-0000", ["SubmittorUserName"] = null, ["DateInspected"] = "2026-10-17T10:00:00-05:00",
        ["StructureTypeId"] = 0, ["SuperintendentName"] = null, ["Signature"] = "/9j/AA==", ["InspectionFee_Secure"] = "PLACEHOLDERCIPHERTEXTA0==",
        ["Sections"] = new JArray(
            new JObject { ["SectionId"] = 1, ["Name"] = "Administrative", ["ControlName"] = "Simple", ["Number"] = "1",
                ["Items"] = new JArray(Item(101, "1.1", "Superintendent", "SearchLookup", "Super A")) },
            new JObject { ["SectionId"] = 5, ["Name"] = "Final", ["ControlName"] = "SectionStandardControl", ["Number"] = "5",
                ["Items"] = new JArray(Item(501, "5.1", "Cleanup", "PassFailNaNi", "Pass", required: true)) }),
        ["Events"] = InputEvents.DeepClone(),
        ["Attachments"] = new JArray()
    };

    private static JObject Read(string path)
    {
        using var reader = new JsonTextReader(new StringReader(File.ReadAllText(path))) { DateParseHandling = DateParseHandling.None };
        return JObject.Load(reader);
    }

    public static async Task<int> Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "red-f026-" + Guid.NewGuid().ToString("N"));
        string inspections = Path.Combine(root, "Inspections");
        foreach (string sub in new[] { "MyList", "Outbox", "Archive" }) Directory.CreateDirectory(Path.Combine(inspections, sub));
        Directory.CreateDirectory(Path.Combine(root, "RedLocal"));
        try
        {
            var chicago = TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");

            // ---------- A-026-01 Descriptions ----------
            var events = new JArray(new[] { (30, (string?)null), (31, null), (33, null), (34, "My List"), (36, null), (40, "Inspect Version 2026.1") }
                .Select(e => new JObject { ["Date"] = "2026-10-13T15:09:41.5-05:00", ["EventTypeId"] = e.Item1, ["Username"] = Username, ["UserDisplayName"] = "Pat Q Example", ["AdditionalInfo"] = e.Item2 }));
            var rows = InspectionHistory.Rows(events, chicago);
            Check("A-026-01", rows.Select(r => r.Description).SequenceEqual(new[] { "Created", "Submitted", "Printed", "Moved to My List", "Approved", "" }) &&
                              rows.All(r => r.User == "Pat Q Example") && InspectionHistory.Title == "Inspection History" &&
                              InspectionHistory.Description(32, null) == "Uploaded to web site" && InspectionHistory.Description(35, "Review") == "Transferred to Review" &&
                              InspectionHistory.Description(41, "x") == "",
                "30 Created, 31 Submitted, 33 Printed, 34 Moved to My List, 36 Approved, 40 empty (and 32, 35, 41 per H2)");

            // ---------- A-026-02 Date format ----------
            Check("A-026-02", rows[0].Date == "10/13/2026 03:09 PM" && InspectionHistory.DateText(JValue.CreateNull()) == "" &&
                              InspectionHistory.DateText(new JValue("2026-10-13T15:09:41"), chicago) == "10/13/2026 03:09 PM",
                $"Date \"2026-10-13T15:09:41.5-05:00\" in that zone shows {rows[0].Date}");

            // ---------- A-026-03 RED writes no session events: open, edit, save, print, submit ----------
            string path = Path.Combine(inspections, "MyList", "1234567-CPP-1-PQE.ins");
            File.WriteAllText(path, Fixture().ToString(Formatting.None), new UTF8Encoding(false));
            var saver = new SurgicalSaveService();
            var model = saver.Load(path);
            model.Sections![1].Items![0].Comments = "Edited in the history check";
            saver.Save(model);
            var printed = ReportService.Render(ReportService.ParseDocument(saver.ProjectCurrent(model).ToUtf8Bytes()), new ReportSettings(), new ReportOptions(false, true, true), DateTime.Now);
            var result = await SubmitService.SubmitAsync(new SubmitContext
            {
                Model = model, Saver = saver, SourcePath = path, Profile = new CompleteProfile(inspections, InspectUserKind.Inspector, "Pat Q Example", Username, "000-000-0000", "TX"),
                RawUserType = "Inspector", Validate = () => new CompletionResult(), LocalRoot = Path.Combine(root, "RedLocal"), Log = new NoLog(), Sleep = _ => { }
            });
            string outbox = Path.Combine(inspections, "Outbox", "1234567-CPP-1-PQE.ins");
            var outEvents = File.Exists(outbox) ? Read(outbox)["Events"] as JArray : null;
            Check("A-026-03", printed.Pdf.Length > 0 && result.Stage == SubmitStage.Done && outEvents != null &&
                              JToken.DeepEquals(outEvents, InputEvents) && (string?)Read(outbox)["Sections"]![1]!["Items"]![0]!["Comments"] == "Edited in the history check",
                $"after open, edit, save, print and submit the Outbox file's Events are exactly [30, 40], unchanged ({result.Stage}: {result.Message})");

            // ---------- A-026-04 Creation event (built by F-021; checked in AddServiceHarness A-021-10) ----------
            string addService = File.ReadAllText(Path.Combine("..", "..", "Services", "AddServiceService.cs"));
            var writers = Directory.GetFiles(Path.Combine("..", ".."), "*.cs", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}tests{Path.DirectorySeparatorChar}") && !f.Contains("red-release-pre-sync-backups") &&
                            !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
                .Where(f => File.ReadAllText(f).Contains("[\"EventTypeId\"] ="))
                .Select(Path.GetFileName).ToList();
            Check("A-026-04", addService.Contains("[\"Username\"] = profile.Username") && addService.Contains("[\"EventTypeId\"] = 30, [\"AdditionalInfo\"] = null") &&
                              writers.SequenceEqual(new[] { "AddServiceService.cs" }),
                $"only the Add wizard writes an event: type 30, Username = profile Username, AdditionalInfo null (writers: {string.Join(", ", writers)})");
        }
        catch (Exception ex)
        {
            _fail++;
            Console.WriteLine("FAIL harness  " + ex);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
        Console.WriteLine();
        Console.WriteLine($"{_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
    }
}
