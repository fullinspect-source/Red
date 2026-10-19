// F-007 header sections harness (program/features/F-007-header-sections/ACCEPTANCE.md).
//   dotnet run -p:Platform=x64
// Synthetic fixtures only (job 1234567, initials PQE, 100 Example St, placeholder fee ciphertexts).
using System.IO;
using System.Text;
using InspectionEditor;
using InspectionEditor.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

return Harness.Run();

static class Harness
{
    private static int _passed, _failed;

    private static void Check(string id, bool ok, string what)
    {
        if (ok) { _passed++; Console.WriteLine($"PASS {id} {what}"); }
        else { _failed++; Console.WriteLine($"FAIL {id} {what}"); }
    }

    private static JObject Fixture(string code = "CPP", int formId = 20) => new()
    {
        ["InspectVersion"] = null, ["WorkOrderId"] = "00000000-0000-0000-0000-000000000001", ["ServiceId"] = 1, ["FormId"] = formId,
        ["IsPreinspection"] = false, ["NextActionId"] = 0, ["NextActionText"] = null, ["InspectionName"] = "Example Inspection",
        ["InspectionNumber"] = $"1234567-{code}-1-PQE", ["InspectionCode"] = code, ["Attempt"] = 1, ["StatusId"] = 2,
        ["StatusList"] = new JArray(
            new JObject { ["Id"] = 0, ["Name"] = "Ordered" }, new JObject { ["Id"] = 1, ["Name"] = "Scheduled" },
            new JObject { ["Id"] = 2, ["Name"] = "Passed" }, new JObject { ["Id"] = 3, ["Name"] = "Failed" },
            new JObject { ["Id"] = 5, ["Name"] = "Correct & Proceed" }),
        ["PlanNumber"] = "P1", ["BuilderName"] = "Example Builder", ["Contact"] = "Example Contact", ["ContactNumber"] = "000-000-0000",
        ["JobNumber"] = "1234567", ["Project"] = "Example Project", ["Address"] = "100 Example St", ["Lot"] = "1", ["Block"] = "A",
        ["City"] = "Example City", ["State"] = "TX", ["InspectorName"] = "Pat Q Example", ["DateInspected"] = "2026-10-13T10:09:17.7656226-05:00",
        ["StructureTypeId"] = 2, ["IsReinspection"] = false, ["PurchaseOrderRequired"] = false, ["PurchaseOrderNumber"] = "",
        ["RequisitionNumber"] = null, ["Requests"] = null, ["WhoNotified"] = null, ["WhenNotified"] = null, ["Signature"] = "/9j/AA==",
        ["InspectionFee_Secure"] = "PLACEHOLDERCIPHERTEXTA0==", ["InspectorOtherFee1_Secure"] = "PLACEHOLDERCIPHERTEXTB0==",
        ["InspectorOtherFee2_Secure"] = "PLACEHOLDERCIPHERTEXTC0==", ["OtherFeeDescription1"] = "Trip", ["OtherFeeDescription2"] = null,
        ["InternalNotes"] = null,
        ["Sections"] = new JArray(new JObject
        {
            ["SectionId"] = 2, ["Name"] = "Footings", ["ControlName"] = "SectionStandardControl", ["Number"] = "2",
            ["Items"] = new JArray(new JObject { ["ItemId"] = 201, ["Name"] = "Forms", ["Number"] = "2.1", ["ControlName"] = "PassFailNaNi", ["Value"] = "Pass", ["Comments"] = "" })
        }),
        ["Events"] = new JArray(), ["Attachments"] = new JArray()
    };

    private static List<string> ChangedKeys(JObject a, JObject b) =>
        a.Properties().Select(p => p.Name).Union(b.Properties().Select(p => p.Name))
            .Where(k => !JToken.DeepEquals(a[k], b[k])).ToList();

    private static JObject Read(string path)
    {
        using var reader = new JsonTextReader(new StringReader(File.ReadAllText(path))) { DateParseHandling = DateParseHandling.None };
        return JObject.Load(reader);
    }

    public static int Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "red-f007-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "MyList"));
        try
        {
            string Place(JObject doc)
            {
                string path = Path.Combine(root, "MyList", (string)doc["InspectionNumber"]! + ".ins");
                File.WriteAllText(path, doc.ToString(Formatting.None), new UTF8Encoding(false));
                return path;
            }

            // ---------- A-007-01 Status list comes from the file ----------
            {
                var list = HeaderRules.StatusList(Fixture()["StatusList"]);
                Check("A-007-01", list.Select(s => s.Name).SequenceEqual(new[] { "Ordered", "Scheduled", "Passed", "Failed", "Correct & Proceed" }) &&
                                  list.Select(s => s.Id).SequenceEqual(new[] { 0, 1, 2, 3, 5 }),
                    "the status combo lists exactly the file's StatusList");
            }

            // ---------- A-007-02 / A-007-03 Next action ----------
            {
                Check("A-007-02", HeaderRules.FailedStatusId == 3 && HeaderRules.NextActions.Count == 4,
                    "Next Action shows only for StatusId 3, with four entries");
                var texts = HeaderRules.NextActions.Select(n => n.Text).ToArray();
                Check("A-007-02", texts.SequenceEqual(new[] { " ", "Failed items to be inspected at next inspection",
                    "Request a reinspection. (PO required)", "Request a reinspection." }), "the four NextActionText values are INSPECT's (E2)");

                string path = Place(Fixture());
                var saver = new SurgicalSaveService();
                var model = saver.Load(path);
                var before = Read(path);
                saver.SetHeaderValue("StatusId", new JValue(3));
                var option = HeaderRules.NextActions[2];
                saver.SetHeaderValue("NextActionId", new JValue(option.Id));
                saver.SetHeaderValue("NextActionText", new JValue(option.Text));
                saver.Save(model);
                var after = Read(path);
                Check("A-007-03", (int)after["NextActionId"]! == 2 && (string?)after["NextActionText"] == "Request a reinspection. (PO required)" &&
                                  ChangedKeys(before, after).OrderBy(k => k).SequenceEqual(new[] { "NextActionId", "NextActionText", "StatusId" }),
                    "picking \"Request a reinspection. (PO required)\" writes NextActionId 2 and the exact text, nothing else");
                File.Delete(path);
            }

            // ---------- A-007-04 Missing next action marker ----------
            {
                var doc = Fixture(); doc["StatusId"] = 3; doc["NextActionId"] = 0;
                var markers = HeaderRules.Markers(k => doc[k]);
                Check("A-007-04", markers.Any(m => m.Key == "NextActionId" && m.Text == "Next action is required." && m.Blocking),
                    "Failed with NextActionId 0 shows \"Next action is required.\"");
                doc["StatusId"] = 0;
                Check("T1", HeaderRules.Markers(k => doc[k]).Any(m => m.Text == "Status cannot be Ordered."), "status Ordered shows \"Status cannot be Ordered.\"");
            }

            // ---------- A-007-05 Lot marker ----------
            {
                var doc = Fixture(); doc["Lot"] = "";
                var lot = HeaderRules.Markers(k => doc[k]).SingleOrDefault(m => m.Key == "Lot");
                Check("A-007-05", lot != null && lot.Text == "Lot required." && !lot.Blocking, "Lot \"\" shows \"Lot required.\" (not blocking)");
                Check("T1", HeaderRules.Markers(k => Fixture()[k]).Count == 0, "a complete, signed fixture has no markers");
                var unsigned = Fixture(); unsigned["Signature"] = null;
                Check("I20", HeaderRules.Markers(k => unsigned[k]).Any(m => m.Key == "Signature" && m.Text == "Signature required."),
                    "Signature null shows \"Signature required.\"");
            }

            // ---------- A-007-06 PO rule marker ----------
            {
                var doc = Fixture(); doc["PurchaseOrderRequired"] = true; doc["PurchaseOrderNumber"] = ""; doc["RequisitionNumber"] = null;
                Check("A-007-06", HeaderRules.Markers(k => doc[k]).Any(m => m.Text == "Purchase Order or Requisition Number required."),
                    "PO required with both numbers blank shows the PO marker");
                doc["RequisitionNumber"] = "R-1";
                Check("A-007-06", !HeaderRules.Markers(k => doc[k]).Any(m => m.Text.StartsWith("Purchase Order")), "a requisition number clears it");
            }

            // ---------- A-007-07 Internal notes saved; A-007-09 fees untouched ----------
            {
                string path = Place(Fixture());
                var saver = new SurgicalSaveService();
                var model = saver.Load(path);
                var before = Read(path);
                saver.SetHeaderValue("InternalNotes", new JValue("Gate code at office"));
                saver.Save(model);
                var after = Read(path);
                Check("A-007-07", (string?)after["InternalNotes"] == "Gate code at office" && ChangedKeys(before, after).SequenceEqual(new[] { "InternalNotes" }),
                    "InternalNotes is written and no other key changed");
                Check("A-007-09", HeaderRules.FeeKeys.All(k => JToken.DeepEquals(before[k], after[k])) &&
                                  File.ReadAllText(path).Contains("\"InspectionFee_Secure\":\"PLACEHOLDERCIPHERTEXTA0==\""),
                    "the three *_Secure strings and both descriptions are byte-identical");
                bool refused = HeaderRules.FeeKeys.All(k =>
                {
                    try { saver.SetHeaderValue(k, new JValue("x")); return false; }
                    catch (InvalidOperationException) { return true; }
                });
                Check("A-007-08", refused, "the header path refuses every fee key (owner Q-21: no fee editor)");
                File.Delete(path);
            }

            // ---------- F-002 A2: a header edit never adds a key the file lacks (Submit may, before Sections) ----------
            {
                var doc = Fixture(); doc.Remove("WhoNotified");
                string path = Place(doc);
                var saver = new SurgicalSaveService();
                var model = saver.Load(path);
                Check("C1", !saver.SetHeaderValue("WhoNotified", JValue.CreateNull()), "null for a missing key changes nothing");
                Check("C1", !saver.SetHeaderValue("WhoNotified", new JValue("Super")) && !saver.HasHeaderKey("WhoNotified"),
                    "an edit of a key the file lacks is refused (F-002 A2)");
                saver.SetHeaderValue("WhenNotified", new JValue(HeaderRules.FormatDate(new DateTime(2026, 10, 19, 9, 30, 0, DateTimeKind.Local))));
                saver.Save(model);
                var after = Read(path);
                Check("C1", after.Property("WhoNotified") == null, "the saved file still has no WhoNotified key");
                saver.SetHeaderValue("SubmittorUserName", new JValue("pqe"), allowAdd: true);
                saver.Save(model);
                var names = Read(path).Properties().Select(p => p.Name).ToList();
                Check("C1", names.IndexOf("SubmittorUserName") == names.IndexOf("Sections") - 1, "Submit's allowAdd puts a missing key just before Sections");
                saver.SetResult(2, 0, null);
                saver.Save(model);
                Check("E5", (string?)Read(path)["NextActionText"] == " ", "the result picker's index 0 writes NextActionText \" \" (E2 table)");
                Check("C2", Regex(@"^2026-10-19T09:30:00[+-]\d\d:\d\d$", (string?)after["WhenNotified"]),
                    $"WhenNotified uses the B5 local format ({after["WhenNotified"]})");
                File.Delete(path);
            }

            // ---------- Structure ids (R-report-layouts L7) ----------
            Check("T3", HeaderRules.Structures.Select(s => $"{s.Id}={s.Name}").SequenceEqual(new[] { "0=Main House", "1=Pavilion", "2=Pool House", "3=Outdoor Kitchen", "4=Addition" }),
                "the Structure combo uses INSPECT's StructureTypeId names");

            // ---------- A-007-10 Result picker respects StatusList ----------
            {
                var options = new List<(ResultChoice, bool, string)>
                {
                    (new ResultChoice("Engineer", 24, 0, null, "#000000"), false, ""),
                    (new ResultChoice("Pass", 2, 0, null, "#000000"), false, "")
                };
                var kept = ResultPickerWindow.FilterByStatusList(options, new[] { 0, 1, 24 });
                Check("A-007-10", kept.Count == 1 && kept[0].Item1.StatusId == 24, "SCI StatusList with 24 and no 2: Engineer Review offered, Pass not");
                Check("E5", ResultPickerWindow.FilterByStatusList(options, null).Count == 2, "a file without a StatusList keeps the picker's options");
            }

            // ---------- A-007-12 Attempt edit rebuilds the number ----------
            {
                string number = HeaderRules.BuildInspectionNumber("1234567", "CPP", 2, "Pat Q Example", false, "PQE");
                Check("A-007-12", number == "1234567-CPP-2-PQE", $"Attempt 2 rebuilds 1234567-CPP-2-PQE ({number})");
                Check("O5", HeaderRules.BuildInspectionNumber("1234567R1", "CPP", 3, "Pat Q Example", true) == "1234567-CPP-P-3-PQE",
                    "IsPreinspection adds -P- and job7 is the first 7 characters");
                Check("O5", HeaderRules.BuildInspectionNumber("1234567", "CPP", 2, "", false, HeaderRules.InitialsOf("1234567-CPP-1-PQE")) == "1234567-CPP-2-PQE",
                    "no InspectorName: the old number's initials are kept");

                string path = Place(Fixture());
                var saver = new SurgicalSaveService();
                var model = saver.Load(path);
                saver.SetHeaderValue("Attempt", new JValue(2));
                saver.SetHeaderValue("InspectionNumber", new JValue(number));
                saver.Save(model);
                var after = Read(path);
                Check("A-007-12", (string?)after["InspectionNumber"] == "1234567-CPP-2-PQE" && File.Exists(path) &&
                                  Path.GetFileName(path) == "1234567-CPP-1-PQE.ins",
                    "the file keeps its name until Submit while InspectionNumber changes");
            }

            // ---------- T1 marker texts ----------
            {
                var empty = new JObject { ["Attempt"] = 0, ["StatusId"] = 2 };
                var texts = HeaderRules.Markers(k => empty[k]).Select(m => m.Text).ToList();
                foreach (var expected in new[] { "Inspection Date required.", "Builder required.", "Project required.", "Address required.",
                    "City required.", "State required.", "S2E Number required.", "Plan Number required.", "Inspector required.",
                    "Attempt must be 1 or greater.", "Lot required.", "Block required.", "Signature required." })
                    Check("T1", texts.Contains(expected), $"marker \"{expected}\"");
            }
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine($"F-007 harness: {_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    private static bool Regex(string pattern, string? text) => text != null && System.Text.RegularExpressions.Regex.IsMatch(text, pattern);
}
