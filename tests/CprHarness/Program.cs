// F-008 CPR Concrete Pour harness (program/features/F-008-cpr-concrete-pour/ACCEPTANCE.md).
//   dotnet run -p:Platform=x64
// Synthetic fixtures only (job 1234567, 100 Example St).
using System.IO;
using System.Text;
using System.Windows;
using InspectionEditor;
using InspectionEditor.Models;
using InspectionEditor.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

int exit = 1;
var thread = new Thread(() => exit = Harness.Run());
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();
return exit;

static class Harness
{
    private static int _passed, _failed;

    private static void Check(string id, bool ok, string what)
    {
        if (ok) { _passed++; Console.WriteLine($"PASS {id} {what}"); }
        else { _failed++; Console.WriteLine($"FAIL {id} {what}"); }
    }

    private static JObject Item(int itemId, string number, string name, string control, string? value, string groupId = "", string? group = null,
        bool hideAll = false, bool required = false) => new()
    {
        ["ItemId"] = itemId, ["Name"] = name, ["Number"] = number, ["ControlName"] = control, ["ItemGroupId"] = groupId,
        ["ItemGroupControlName"] = group, ["IsCopied"] = false, ["Value"] = value, ["ResultSortOrder"] = 0, ["Comments"] = "",
        ["Pictures"] = new JArray(), ["Required"] = required, ["HideCommentsButton"] = hideAll, ["HidePicturesButton"] = hideAll,
        ["HideAddButton"] = hideAll, ["IsPictureRequired"] = false
    };

    private static JObject Cpr(int formId = 27, int serviceId = 61, string? cylinders = null)
    {
        var trucks = new JArray();
        for (int n = 1; n <= 15; n++)
            foreach (var type in new[] { "Arrival", "Gallons", "Slump", "Temp" })
                trucks.Add(Item(4000 + n * 10 + type[0], $"4.{n}.{type[0]}", $"Truck {n} {type}", "PassFail", "", "1", "TruckPlacement"));
        return new JObject
        {
            ["InspectVersion"] = null, ["ServiceId"] = serviceId, ["FormId"] = formId, ["InspectionNumber"] = "1234567-CPR-1-XX",
            ["InspectionCode"] = "CPR", ["Attempt"] = 1, ["StatusId"] = 2, ["JobNumber"] = "1234567", ["Address"] = "100 Example St",
            ["InspectionFee_Secure"] = "PLACEHOLDERCIPHERTEXTA0==",
            ["Sections"] = new JArray(
                new JObject { ["SectionId"] = 1, ["Name"] = "Administrative", ["ControlName"] = "Simple", ["Number"] = "1",
                    ["Items"] = new JArray(Item(1615, "1.0", "Test cylinders included", "CPRYesNo", cylinders, required: true),
                                           Item(1601, "1.1", "Pump truck used", "YesNo", null)) },
                new JObject { ["SectionId"] = 4, ["Name"] = "Truck Placement", ["ControlName"] = "SectionStandardControl", ["Number"] = "4", ["Items"] = trucks },
                new JObject { ["SectionId"] = 5, ["Name"] = "Placement", ["ControlName"] = "SectionStandardControl", ["Number"] = "5",
                    ["Items"] = new JArray(Item(1702, "5.2", "Placement Diagram", "FoundationSketch", null, hideAll: true, required: true)) }),
            ["Events"] = new JArray(), ["Attachments"] = new JArray()
        };
    }

    // ChecklistItemVisibility is internal to RED.
    private static bool ShouldShow(Item item) => (bool)typeof(InkItemService).Assembly
        .GetType("InspectionEditor.Services.ChecklistItemVisibility")!.GetMethod("ShouldShow")!.Invoke(null, new object[] { item })!;

    private static JObject Read(string path)
    {
        using var reader = new JsonTextReader(new StringReader(File.ReadAllText(path))) { DateParseHandling = DateParseHandling.None };
        return JObject.Load(reader);
    }

    private static List<string> ChangedTopLevel(JObject a, JObject b) =>
        a.Properties().Select(p => p.Name).Union(b.Properties().Select(p => p.Name))
            .Where(k => k != "Sections" && !JToken.DeepEquals(a[k], b[k])).ToList();

    public static int Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "red-f008-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "MyList"));
        string Place(JObject doc, string name)
        {
            string path = Path.Combine(root, "MyList", name);
            File.WriteAllText(path, doc.ToString(Formatting.None), new UTF8Encoding(false));
            return path;
        }
        SurgicalSaveService NewSaver()
        {
            var saver = new SurgicalSaveService();
            CprRules.Install(saver);
            return saver;
        }
        Item Cylinders(InspectionFile model) => model.Sections[0].Items[0];

        try
        {
            // ---------- A-008-01 / A-008-02 ----------
            {
                string path = Place(Cpr(), "a.ins");
                var saver = NewSaver();
                var model = saver.Load(path);
                var before = Read(path);
                Cylinders(model).Value = "Yes"; // a direct tap
                saver.Save(model);
                var after = Read(path);
                Check("A-008-01", (string?)after["Sections"]![0]!["Items"]![0]!["Value"] == "Yes" && (int)after["ServiceId"]! == 77 &&
                                  ChangedTopLevel(before, after).SequenceEqual(new[] { "ServiceId" }),
                    "Yes on item 1615 stores \"Yes\" and ServiceId 77; no other top-level key changed");
                Cylinders(model).Value = "No";
                saver.Save(model);
                Check("A-008-02", (int)Read(path)["ServiceId"]! == 61, "No sets ServiceId 61");
                Cylinders(model).Value = null; // clearing
                saver.Save(model);
                Check("P3", (int)Read(path)["ServiceId"]! == 61, "clearing item 1615 leaves ServiceId unchanged");
                Cylinders(model).Value = "Yes";
                saver.Save(model);
                Check("P3", (int)Read(path)["ServiceId"]! == 77, "Yes again after a clear sets 77");
            }

            // ---------- A-008-03 Bulk path applies the rule ----------
            {
                string path = Place(Cpr(), "b.ins");
                var saver = NewSaver();
                var model = saver.Load(path);
                // Template apply, section "All" labels, AI fill: every path ends as a model Value change before a save.
                foreach (var item in model.Sections[0].Items) item.Value = "Yes";
                saver.Save(model);
                Check("A-008-03", (int)Read(path)["ServiceId"]! == 77, "item 1615 set to Yes by a bulk path gives ServiceId 77");
            }

            // ---------- A-008-04 Anomalous files untouched on open ----------
            {
                string path = Place(Cpr(cylinders: "Yes"), "c.ins");
                byte[] original = File.ReadAllBytes(path);
                var saver = NewSaver();
                var model = saver.Load(path);
                saver.Save(model);
                Check("A-008-04", File.ReadAllBytes(path).SequenceEqual(original), "Yes/61 file: open + close leaves the bytes unchanged");
                model.Sections[0].Items[1].Value = "No"; // edit another item
                saver.Save(model);
                Check("SPEC §4", (int)Read(path)["ServiceId"]! == 61, "an edit elsewhere keeps the anomalous ServiceId 61");
            }

            // ---------- A-008-05 Truck log count fixed ----------
            {
                var section = Cpr().ToObject<InspectionFile>()!.Sections[1];
                var run = ItemGroupLayout.Detect(section).Single();
                Check("A-008-05", section.Items.Count == 60 && run.Recognized && section.Items.All(ItemGroupLayout.IsGroupedItem),
                    "section 4 is the 60-cell grid and every cell is grouped, so Copy/Remove are refused (F-005 G4)");
            }

            // ---------- A-008-06 Placement Diagram caption ----------
            {
                var model = Cpr().ToObject<InspectionFile>()!;
                var diagram = model.Sections[2].Items[0];
                diagram.Pictures = new List<Picture>();
                var window = new InkItemEditorWindow(diagram, null, _ => { }, () => null, _ => { })
                {
                    WindowStartupLocation = WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false
                };
                window.Show();
                window.UpdateLayout();
                window.AddStroke(new[] { new Point(100, 100), new Point(400, 300) });
                window.Close();
                Check("A-008-06", diagram.Pictures.Count == 1 && diagram.Pictures[0].Title == "5.2 Placement Diagram" && (string?)diagram.Value == "ok",
                    "one picture, caption \"5.2 Placement Diagram\", Value \"ok\"");
            }

            // ---------- A-008-08 Placement Diagram row visible ----------
            {
                var diagram = Cpr().ToObject<InspectionFile>()!.Sections[2].Items[0];
                diagram.Pictures = new List<Picture>();
                Check("A-008-08", diagram.HidePicturesButton && diagram.HideCommentsButton && ShouldShow(diagram),
                    "5.2 with all three Hide*Button flags and no picture is shown");
            }

            // ---------- A-008-07 Plain CPR variants ----------
            foreach (int formId in new[] { 255, 148 })
            {
                var doc = Cpr(formId, formId == 148 ? 330 : 61);
                ((JArray)doc["Sections"]!).RemoveAt(2);
                ((JArray)doc["Sections"]!).RemoveAt(1);
                string path = Place(doc, $"plain{formId}.ins");
                var saver = NewSaver();
                var model = saver.Load(path);
                Cylinders(model).Value = "Yes";
                saver.Save(model);
                var after = Read(path);
                bool noGrid = model.Sections.All(s => ItemGroupLayout.Detect(s).Count == 0);
                bool noInk = model.Sections.SelectMany(s => s.Items).All(i => !InkItemService.IsInk(i));
                Check("A-008-07", noGrid && noInk && (int)after["ServiceId"]! == (formId == 148 ? 330 : 61),
                    $"FormId {formId}: no grid, no FoundationSketch, no ServiceId switch");
            }
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine($"F-008 harness: {_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }
}
