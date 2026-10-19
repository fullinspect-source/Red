// F-010 SCI Special Consult / SCHA harness (program/features/F-010-sci-special-consult/ACCEPTANCE.md).
//   dotnet run -p:Platform=x64
// Synthetic fixtures only (job 1234567, initials PQE, 100 Example St, placeholder fee ciphertexts). The real 13 SCI and
// 2 SCHA files were not copied (privacy guard), so V1 on production copies is not verified here.
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Controls;
using InspectionEditor;
using InspectionEditor.Models;
using InspectionEditor.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

int result = 1;
var thread = new Thread(() => result = Harness.Run());
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();
return result;

static class Harness
{
    private static int _passed, _failed;

    private static void Check(string id, bool ok, string what)
    {
        if (ok) { _passed++; Console.WriteLine($"PASS {id} {what}"); }
        else { _failed++; Console.WriteLine($"FAIL {id} {what}"); }
    }

    private static JObject Header(string code, int formId, int serviceId, JArray statusList) => new()
    {
        ["InspectVersion"] = null, ["WorkOrderId"] = "00000000-0000-0000-0000-000000000010", ["ServiceId"] = serviceId, ["FormId"] = formId,
        ["IsPreinspection"] = false, ["NextActionId"] = 0, ["NextActionText"] = null, ["InspectionName"] = "Special Consult",
        ["InspectionNumber"] = $"1234567-{code}-1-PQE", ["InspectionCode"] = code, ["Attempt"] = 1, ["StatusId"] = 1,
        ["StatusList"] = statusList,
        ["PlanNumber"] = "P1", ["BuilderName"] = "Example Builder", ["Contact"] = "Example Contact", ["ContactNumber"] = "000-000-0000",
        ["JobNumber"] = "1234567", ["Project"] = "Example Project", ["Address"] = "100 Example St", ["Lot"] = "1", ["Block"] = "A",
        ["City"] = "Example City", ["State"] = "TX", ["InspectorName"] = "Pat Q Example", ["DateInspected"] = "2026-10-13T10:09:17.7656226-05:00",
        ["StructureTypeId"] = 0, ["IsReinspection"] = false, ["PurchaseOrderRequired"] = false, ["PurchaseOrderNumber"] = "",
        ["RequisitionNumber"] = null, ["Requests"] = null, ["WhoNotified"] = null, ["WhenNotified"] = null, ["Signature"] = null,
        ["InspectionFee_Secure"] = "PLACEHOLDERCIPHERTEXTA0==", ["InspectorOtherFee1_Secure"] = "PLACEHOLDERCIPHERTEXTB0==",
        ["InspectorOtherFee2_Secure"] = "PLACEHOLDERCIPHERTEXTC0==", ["OtherFeeDescription1"] = null, ["OtherFeeDescription2"] = null,
        ["InternalNotes"] = null,
    };

    private static JArray Statuses(params (int Id, string Name)[] list) =>
        new(list.Select(s => new JObject { ["Id"] = s.Id, ["Name"] = s.Name }));

    private static JObject Item(int itemId, string number, string name, string? control, JToken? value, string? comments,
        bool required = false, bool alwaysPrint = false) => new()
    {
        ["ItemId"] = itemId, ["Name"] = name, ["Number"] = number, ["ControlName"] = control, ["Value"] = value,
        ["Comments"] = comments, ["Required"] = required, ["AlwaysPrint"] = alwaysPrint, ["IsCopied"] = false,
        ["HidePicturesButton"] = false, ["HideCommentsButton"] = false, ["Pictures"] = new JArray()
    };

    // SCI FormId 44: two Simple sections, 20 Memo rows (only 1.1 Required, all AlwaysPrint) and one Sketch (2.1).
    private static JObject Sci44()
    {
        var doc = Header("SCI", 44, 33, Statuses((0, "Ordered"), (1, "Scheduled"), (24, "For Engineer Review")));
        var s1 = new JArray();
        for (int i = 1; i <= 10; i++)
        {
            JToken value = i == 4 ? "ok" : i == 6 ? "" : JValue.CreateNull();
            string? comments = i == 4 ? "Slab crack 2 mm" : i == 6 ? "See photos" : null;
            s1.Add(Item(4400 + i, $"1.{i}", $"Comment {i}", "Memo", value, comments, required: i == 1, alwaysPrint: true));
        }
        var s2 = new JArray { Item(4500, "2.1", "Additional Information", "Sketch", JValue.CreateNull(), null) };
        for (int i = 11; i <= 20; i++)
            s2.Add(Item(4400 + i, $"2.{i - 9}", $"Comment {i}", "Memo", JValue.CreateNull(), null, alwaysPrint: true));
        doc["Sections"] = new JArray(
            new JObject { ["SectionId"] = 1, ["Name"] = "Special Consult", ["ControlName"] = "SectionStandardControl", ["Number"] = "1", ["Items"] = s1 },
            new JObject { ["SectionId"] = 2, ["Name"] = "Additional Information", ["ControlName"] = "SectionStandardControl", ["Number"] = "2", ["Items"] = s2 });
        doc["Events"] = new JArray(); doc["Attachments"] = new JArray();
        return doc;
    }

    // SCHA FormId 312: Text / YesNo / YesNoNaNi / Lookup rows, one section with a null ControlName, four items numbered 1.3.
    private static JObject Scha312()
    {
        var doc = Header("SCHA", 312, 33, Statuses((0, "Ordered"), (1, "Scheduled"), (2, "Passed"), (3, "Failed"), (5, "Correct & Proceed"), (9, "Cancelled")));
        var items = new JArray
        {
            Item(3101, "1.1", "Relative humidity", "Text", "", null),
            Item(3102, "1.2", "Dehumidifier present", "YesNo", JValue.CreateNull(), null),
            Item(3103, "1.3", "Reading location A", "Text", "", null),
            Item(3104, "1.3", "Reading location B", "Text", "", null),
            Item(3105, "1.3", "Reading location C", "Text", "", null),
            Item(3106, "1.3", "Reading location D", "YesNoNaNi", JValue.CreateNull(), null),
            Item(3107, "1.4", "Meter type", "Lookup", JValue.CreateNull(), null),
        };
        doc["Sections"] = new JArray(
            new JObject { ["SectionId"] = 31, ["Name"] = "Humidity Assessment", ["ControlName"] = null, ["Number"] = "1", ["Items"] = items });
        doc["Events"] = new JArray(); doc["Attachments"] = new JArray();
        return doc;
    }

    // ChecklistItemVisibility is internal to RED; the harness calls the real method.
    private static readonly Func<Item, bool> ShouldShowMethod = typeof(SurgicalSaveService).Assembly
        .GetType("InspectionEditor.Services.ChecklistItemVisibility")!.GetMethod("ShouldShow")!.CreateDelegate<Func<Item, bool>>();
    private static bool ShouldShow(Item item) => ShouldShowMethod(item);

    private static JObject Read(string path)
    {
        using var reader = new JsonTextReader(new StringReader(File.ReadAllText(path))) { DateParseHandling = DateParseHandling.None };
        return JObject.Load(reader);
    }

    // Every JSON path whose value differs between two documents.
    private static List<string> Diff(JToken a, JToken b, string path = "")
    {
        var result = new List<string>();
        if (a is JObject oa && b is JObject ob)
        {
            foreach (string key in oa.Properties().Select(p => p.Name).Union(ob.Properties().Select(p => p.Name)))
            {
                var va = oa[key]; var vb = ob[key];
                if (va == null || vb == null) { result.Add(path + "." + key); continue; }
                result.AddRange(Diff(va, vb, path + "." + key));
            }
        }
        else if (a is JArray aa && b is JArray ab && aa.Count == ab.Count)
        {
            for (int i = 0; i < aa.Count; i++) result.AddRange(Diff(aa[i], ab[i], $"{path}[{i}]"));
        }
        else if (!JToken.DeepEquals(a, b))
        {
            result.Add(path);
        }
        return result;
    }

    public static int Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "red-f010-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "MyList"));
        try
        {
            string Place(JObject doc)
            {
                string path = Path.Combine(root, "MyList", (string)doc["InspectionNumber"]! + ".ins");
                File.WriteAllText(path, doc.ToString(Formatting.None), new UTF8Encoding(false));
                return path;
            }

            // ---------- A-010-01 Memo narrative shown from Comments ----------
            {
                string path = Place(Sci44());
                var model = new SurgicalSaveService().Load(path);
                var item = model.Sections[0].Items[3];
                Check("A-010-01", item.Number == "1.4" && ItemControls.For(item).Kind == ItemControlKind.Memo &&
                                  item.Comments == "Slab crack 2 mm" && Equals(ItemControls.ValueText(item), "ok"),
                    "item 1.4 is a Memo whose text comes from Comments (Slab crack 2 mm), Value ok");
                Check("N8", model.Sections.SelectMany(s => s.Items).Count(i => i.ControlName == "Memo") == 20 &&
                            model.Sections.SelectMany(s => s.Items).Where(i => i.ControlName == "Memo").All(ShouldShow) &&
                            ShouldShow(model.Sections[1].Items[0]),
                    "all 20 Memo rows and the Sketch row are shown");
                File.Delete(path);
            }

            // ---------- A-010-02 Memo edit writes Comments and ok ----------
            {
                string path = Place(Sci44());
                var before = Read(path);
                var saver = new SurgicalSaveService();
                var model = saver.Load(path);
                var item = model.Sections[0].Items[4];
                // The F-004 Memo editor path: Comments takes the text, Value follows it.
                item.Comments = "See sketch";
                item.Value = ItemControls.MemoValueFor(item.Comments);
                saver.Save(model);
                var after = Read(path);
                var changes = Diff(before, after);
                Check("A-010-02", item.Number == "1.5" && (string?)after["Sections"]![0]!["Items"]![4]!["Comments"] == "See sketch" &&
                                  (string?)after["Sections"]![0]!["Items"]![4]!["Value"] == "ok" &&
                                  changes.OrderBy(c => c).SequenceEqual(new[] { ".Sections[0].Items[4].Comments", ".Sections[0].Items[4].Value" }),
                    "typing \"See sketch\" in 1.5 writes Comments and Value ok, nothing else (V2)");
                File.Delete(path);
            }

            // ---------- A-010-07 Memo text with blank Value is not rewritten ----------
            {
                string path = Place(Sci44());
                byte[] bytes = File.ReadAllBytes(path);
                var saver = new SurgicalSaveService();
                var model = saver.Load(path);
                var item = model.Sections[0].Items[5];
                var rewritten = ItemControls.RewriteNaOnOpen(model);
                Check("A-010-07", item.Comments == "See photos" && Equals(ItemControls.ValueText(item), "") && rewritten.Count == 0,
                    "Comments \"See photos\" with Value \"\" opens as is; Value is not derived from Comments (N10)");
                saver.Save(model);
                Check("A-010-07", File.ReadAllBytes(path).AsSpan().SequenceEqual(bytes), "open and close without edits leaves the bytes unchanged (V1)");
                File.Delete(path);
            }

            // ---------- A-010-05 SCHA null-ControlName section, duplicate numbers ----------
            {
                string path = Place(Scha312());
                var before = Read(path);
                var saver = new SurgicalSaveService();
                var model = saver.Load(path);
                var section = model.Sections[0];
                Check("A-010-05", section.ControlName == null && section.Items.Count == 7 && section.Items.All(ShouldShow) &&
                                  ItemGroupLayout.Detect(section).Count == 0,
                    "the null-ControlName section opens with all 7 items as ordinary (Simple) rows");
                var third = section.Items[4];
                third.Value = "48";
                saver.Save(model);
                var changes = Diff(before, Read(path));
                Check("N6", third.Name == "Reading location C" &&
                            changes.SequenceEqual(new[] { ".Sections[0].Items[4].Value" }),
                    "editing the third of four items numbered 1.3 writes that item only (items are identified by position)");
                File.Delete(path);
            }

            // ---------- A-010-06 Engineer Review follows StatusList ----------
            {
                var ids = HeaderRules.StatusList(Sci44()["StatusList"]).Select(s => s.Id).ToArray();
                Check("N7", ids.SequenceEqual(new[] { 0, 1, 24 }), "the SCI StatusList is {0, 1, 24}");
                var offered = PickerStatusIds("SCI", ids);
                Check("A-010-06", offered.SequenceEqual(new[] { 24 }), "the result picker offers For Engineer Review (24) only");
                var notInList = PickerStatusIds("SCI", new[] { 0, 1, 2 });
                Check("A-010-06", notInList.Count == 0, "with a StatusList that lacks 24, Engineer Review is not offered (F-007 E5)");

                string path = Place(Sci44());
                var before = Read(path);
                var saver = new SurgicalSaveService();
                var model = saver.Load(path);
                saver.SetHeaderValue("StatusId", new JValue(24));
                saver.Save(model);
                var after = Read(path);
                Check("A-010-06", (int)after["StatusId"]! == 24 && Diff(before, after).SequenceEqual(new[] { ".StatusId" }),
                    "choosing it writes StatusId 24 and nothing else");
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            Check("harness", false, "unexpected exception: " + ex);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine($"F-010 harness: {_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    // The StatusIds of the buttons the real result picker builds (CSV config absent, as on a fresh PC).
    private static List<int> PickerStatusIds(string code, IReadOnlyCollection<int> statusList)
    {
        var window = new ResultPickerWindow(code, 20, 0, null, false, statusList);
        var panel = (Panel)typeof(ResultPickerWindow).GetField("ButtonPanel", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        var ids = new List<int>();
        foreach (var child in panel.Children)
        {
            var button = child as Button ?? (child as Panel)?.Children.OfType<Button>().FirstOrDefault();
            if (button?.Tag is { } tag)
                ids.Add((int)tag.GetType().GetProperty("StatusId")!.GetValue(tag)!);
        }
        window.Close();
        return ids;
    }
}
