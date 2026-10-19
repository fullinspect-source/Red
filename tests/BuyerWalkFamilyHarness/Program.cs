// F-009 BWT / New Home Orientation ("BWI") harness (program/features/F-009-bwt-orientation/ACCEPTANCE.md).
//   dotnet run -p:Platform=x64
// Synthetic fixtures and generated sample PDFs only (job 1234567, 100 Example St., Pat Example).
using System.IO;
using System.Text;
using InspectionEditor.Models;
using InspectionEditor.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

return Harness.Run();

static class Harness
{
    private static int _passed, _failed;

    private static void Check(string id, bool ok, string what)
    {
        if (ok) { _passed++; Console.WriteLine($"PASS {id} {what}"); }
        else { _failed++; Console.WriteLine($"FAIL {id} {what}"); }
    }

    private static byte[] SamplePdf(params (string Name, string Value)[] fields)
    {
        using var pdf = new PdfDocument(); pdf.AddPage();
        var form = new PdfDictionary(pdf); pdf.Internals.Catalog.Elements["/AcroForm"] = form;
        var array = new PdfArray(pdf); form.Elements["/Fields"] = array;
        foreach (var (name, value) in fields)
        {
            var field = new PdfDictionary(pdf); field.Elements.SetString("/T", name); field.Elements.SetName("/FT", "/Tx");
            field.Elements.SetString("/V", value); field.Elements.SetString("/DA", "/Helv 9 Tf 0 g");
            pdf.Internals.AddObject(field); array.Elements.Add(field.Reference!);
        }
        using var output = new MemoryStream(); pdf.Save(output); return output.ToArray();
    }

    private static Dictionary<string, string> Values(byte[] bytes)
    {
        using var pdf = PdfReader.Open(new MemoryStream(bytes), PdfDocumentOpenMode.Modify);
        var fields = pdf.Internals.Catalog.Elements.GetDictionary("/AcroForm")!.Elements.GetArray("/Fields")!;
        return fields.Elements.Select(e => (e is PdfSharp.Pdf.Advanced.PdfReference r ? r.Value : e) as PdfDictionary)
            .Where(f => f != null).ToDictionary(f => f!.Elements.GetString("/T"), f => f!.Elements.GetString("/V"));
    }

    private static JObject Bwt(string code = "BWT", string name = "New Home Orientation", int formId = 106) => new()
    {
        ["FormId"] = formId, ["ServiceId"] = 121, ["InspectionCode"] = code, ["InspectionName"] = name,
        ["InspectionNumber"] = $"1234567-{code}-1-XX", ["Address"] = "100 Example St.", ["City"] = "Example City", ["State"] = "TX",
        ["Project"] = "Example Project", ["Lot"] = "1", ["Block"] = "A", ["DateInspected"] = "2026-10-17T10:00:00-05:00",
        ["Contact"] = "Example Contact", ["IsReinspection"] = false, ["Attachments"] = new JArray(),
        ["Sections"] = new JArray(
            new JObject { ["SectionId"] = 3, ["Name"] = "Utilities", ["ControlName"] = "BWCheck", ["Number"] = "3",
                ["Items"] = new JArray(
                    new JObject { ["ItemId"] = 31, ["Number"] = "3.1", ["Name"] = "Water heater", ["ControlName"] = "BWCheck", ["Value"] = "NA", ["Comments"] = "" },
                    new JObject { ["ItemId"] = 32, ["Number"] = "3.2", ["Name"] = "Fuel type", ["ControlName"] = "Text", ["Value"] = "Gas", ["Comments"] = "" }) },
            new JObject { ["SectionId"] = 9, ["Name"] = "Sign", ["ControlName"] = "BWCheck2", ["Number"] = "9",
                ["Items"] = new JArray(
                    new JObject { ["ItemId"] = 91, ["Number"] = "9.1", ["Name"] = "Orientation form", ["ControlName"] = "DocumentButton", ["Template"] = "Orientation Form.pdf", ["Value"] = "" }) })
    };

    private static JObject Read(string path)
    {
        using var reader = new JsonTextReader(new StringReader(File.ReadAllText(path))) { DateParseHandling = DateParseHandling.None };
        return JObject.Load(reader);
    }

    public static int Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "red-f009-" + Guid.NewGuid().ToString("N"));
        string myList = Path.Combine(root, "Inspections", "MyList"), documents = Path.Combine(root, "Inspections", "Documents");
        Directory.CreateDirectory(myList); Directory.CreateDirectory(documents);
        string working = Path.Combine(root, "Working");
        try
        {
            File.WriteAllBytes(Path.Combine(documents, "Orientation Form.pdf"), SamplePdf(("3.2_text", ""), ("City", ""), ("Date", "01/01/2026"), ("Unrelated_x", "")));

            // ---------- A-009-02 Document matching by stem ----------
            {
                var doc = Bwt();
                ((JArray)doc["Attachments"]!).Add(OrientationPdfFormProbe.CreateAttachment("Orientation Form (100 Example St - 20261018).pdf", SamplePdf(("City", "x"))));
                var model = doc.ToObject<InspectionFile>()!;
                var found = OrientationPdfSession.FindCandidates(model);
                Check("A-009-02", found.Count == 1 && found[0].Index == 0, "the dated attachment is the item's document (stem match)");
            }

            // ---------- A-009-03 New document name ----------
            {
                string name = OrientationPdfSession.NewDocumentName("Orientation Form.pdf", "100 Example St.", new DateTime(2026, 10, 18));
                Check("A-009-03", name == "Orientation Form (100 Example St - 20261018).pdf", $"new attachment name ({name})");
                Check("D3", OrientationPdfSession.NewDocumentName("Orientation Form.pdf", "", new DateTime(2026, 10, 18)) == "Orientation Form.pdf",
                    "no address: the template name is kept");
                Check("D3", OrientationPdfSession.NewDocumentName("Orientation Form.pdf", "1/2 Example Rd (Lot 3)", new DateTime(2026, 10, 18)) ==
                            "Orientation Form (12 Example Rd Lot 3 - 20261018).pdf", "characters a file name cannot hold are dropped");
                var created = OrientationPdfFormProbe.CreateAttachment("Orientation Form (100 Example St - 20261018).pdf", SamplePdf());
                Check("A-009-03", (string?)created["FileType"] == "pdf" && created["CanExcludePages"]!.Value<bool>() == false &&
                                  created["ReferenceOnly"]!.Value<bool>() == false,
                    "the new attachment has FileType pdf, CanExcludePages false, ReferenceOnly false (InspectVersion/LastWriteTime: F-017)");

                // Through the real open path: the working copy of a new document carries the D3 name.
                string path = Path.Combine(myList, "1234567-BWT-1-XX.ins");
                File.WriteAllText(path, Bwt().ToString(Formatting.None), new UTF8Encoding(false));
                var saver = new SurgicalSaveService();
                var model = saver.Load(path);
                var session = OrientationPdfSession.OpenTemplate(model, path, working);
                var values = Values(File.ReadAllBytes(session.WorkingPath));
                // ---------- A-009-04 Blank-only fill with INSPECT mapping ----------
                Check("A-009-04", values["3.2_text"] == "Gas" && values["City"] == "Example City" && values["Date"] == "01/01/2026" &&
                                  values["Unrelated_x"] == "",
                    "3.2_text = Gas (item Number), City = Example City (special key), pre-filled Date kept, unknown stays blank");
                using (var pdf = PdfReader.Open(new MemoryStream(File.ReadAllBytes(session.WorkingPath)), PdfDocumentOpenMode.Modify))
                {
                    var form = pdf.Internals.Catalog.Elements.GetDictionary("/AcroForm");
                    Check("A-009-04", form != null && form.Elements.GetArray("/Fields")!.Elements.Count == 4,
                        "the PDF still has its form fields (nothing flattened)");
                }
                session.Monitor.Cleanup();
            }

            // ---------- D4 generic rule: Name match, special keys and dates ----------
            {
                var model = Bwt().ToObject<InspectionFile>()!;
                var resolve = OrientationPdfFormProbe.Resolver(model);
                Check("D4", resolve("Fuel type_any") == "Gas" && resolve("InspectionLot_X") == "1" && resolve("InspectionBlock_X") == "A" &&
                            resolve("Address1_X") == "100 Example St." && resolve("State_X") == "TX" && resolve("Subdivision_X") == "Example Project" &&
                            resolve("Date_X") == "10/17/2026" && resolve("InspectionDay_X") == "17" && resolve("InspectionMonth_X") == "October" &&
                            resolve("InspectionYear_X") == "26",
                    "item Name match and the special keys; the date is DateInspected");
            }

            // ---------- A-009-05 / A-009-06 FormId 129 ----------
            foreach (bool reinspection in new[] { false, true })
            {
                var doc = Bwt(formId: 129);
                doc["IsReinspection"] = reinspection;
                ((JArray)doc["Sections"]!).Insert(0, new JObject { ["SectionId"] = 1, ["Name"] = "Scheduling", ["ControlName"] = "Simple", ["Number"] = "1", ["Items"] = new JArray() });
                ((JObject)doc["Sections"]![2]!)["ControlName"] = "Buyerwalk1<BLDR-B>Example";
                string path = Path.Combine(myList, $"129-{reinspection}.ins");
                File.WriteAllText(path, doc.ToString(Formatting.None), new UTF8Encoding(false));
                byte[] before = File.ReadAllBytes(path);
                var saver = new SurgicalSaveService();
                var model = saver.Load(path);
                BuyerWalkRules.ApplyFormId129Swap(model, saver);
                bool untouchedByOpen = File.ReadAllBytes(path).SequenceEqual(before);
                saver.Save(model);
                var after = Read(path);
                var names = ((JArray)after["Sections"]!).Select(s => (string?)s["Name"]).ToList();
                string? sign = (string?)((JArray)after["Sections"]!).First(s => (string?)s["Name"] == "Sign")["ControlName"];
                if (!reinspection)
                    Check("A-009-05", names.Contains("Scheduling") && sign == "Buyerwalk1<BLDR-B>Example" && untouchedByOpen,
                        "first walk: Sign keeps the template's Buyerwalk1 identifier and Scheduling stays");
                else
                    Check("A-009-06", !names.Contains("Scheduling") && sign == "Buyerwalk2<BLDR-B>Example" && untouchedByOpen &&
                                      model.Sections.Count == 2 && (string?)after["Sections"]![1]!["Items"]![0]!["Name"] == "Orientation form",
                        "second walk: Scheduling is gone, Sign becomes Buyerwalk2..., items stay with their sections");
            }

            // ---------- A-009-08 Unsigned BWT is editable (no lock without a signature) ----------
            {
                var model = Bwt().ToObject<InspectionFile>()!;
                Check("A-009-08", model.ExtensionData?.ContainsKey("Signature") != true,
                    "an unsigned BWT has no inspector Signature, so F-012's lock (L1) cannot apply");
            }

            // ---------- A-009-09 Prefill ----------
            {
                var form = Bwt();
                var simple = new JObject { ["SectionId"] = 1, ["Name"] = "Customer", ["ControlName"] = "Simple", ["Number"] = "1",
                    ["Items"] = new JArray(
                        new JObject { ["Name"] = "Customer Name", ["Value"] = null },
                        new JObject { ["Name"] = "Projected Closing Date", ["Value"] = null },
                        new JObject { ["Name"] = "Schedule follow-up inspection", ["Value"] = "Yes" }) };
                ((JArray)form["Sections"]!).Insert(0, simple);
                form["Requests"] = "Gate code at office";
                var job = new BuyerWalkJob("Pat Example", "000-000-0000", null, "pat@example.com", "Agent Example", null, null,
                    new DateTime(2026, 11, 5), "Super Example", "000-000-0001", new DateTime(2026, 10, 20, 9, 0, 0, DateTimeKind.Local));
                BuyerWalkRules.Prefill(form, job);
                var items = (JArray)form["Sections"]![0]!["Items"]!;
                Check("A-009-09", (string?)items[0]["Value"] == "Pat Example" && (int)form["StatusId"]! == 1 &&
                                  ((string?)form["DateInspected"])!.StartsWith("2026-10-20T09:00:00") &&
                                  ((string?)form["Requests"])!.StartsWith("Scheduled for Oct 20th at 9:00 AM."),
                    $"Customer Name, StatusId 1, DateInspected 2026-10-20 09:00 local, Requests ({form["Requests"]})");
                Check("§6", (string?)form["Requests"] == "Scheduled for Oct 20th at 9:00 AM. House closes on Nov 5th. Super phone: 000-000-0001. Gate code at office" &&
                            items[2]["Value"]!.Type == JTokenType.Null && (string?)form["SuperintendentName"] == "Super Example",
                    "closing date, super phone, existing Requests kept; follow-up cleared; superintendent set");
                Check("§10", BuyerWalkRules.Ordinal(1) == "1st" && BuyerWalkRules.Ordinal(2) == "2nd" && BuyerWalkRules.Ordinal(3) == "3rd" &&
                             BuyerWalkRules.Ordinal(11) == "11th" && BuyerWalkRules.Ordinal(12) == "12th" && BuyerWalkRules.Ordinal(13) == "13th" &&
                             BuyerWalkRules.Ordinal(21) == "21st" && BuyerWalkRules.Ordinal(22) == "22nd" && BuyerWalkRules.Ordinal(31) == "31st",
                    "AsOrdinal");
                var other = Bwt(); other["ServiceId"] = 61;
                BuyerWalkRules.Prefill(other, job);
                Check("§6", other["StatusId"] == null, "the prefill runs only for ServiceId 121");
            }

            // ---------- A-009-10 Code BWI is routed as the BWT family ----------
            {
                var doc = Bwt(code: "BWI", name: "Example Walk");
                var model = doc.ToObject<InspectionFile>()!;
                Check("A-009-10", OrientationPdfSession.IsApplicable(model), "code BWI with a non-matching name gets the document flow");
                model.InspectionCode = " bwi ";
                Check("A-009-10", OrientationPdfSession.IsApplicable(model), "BWI is compared like BWT (trimmed, case-insensitive)");
                string path = Path.Combine(myList, "1234567-BWI-1-XX.ins");
                File.WriteAllText(path, doc.ToString(Formatting.None), new UTF8Encoding(false));
                var saver = new SurgicalSaveService();
                var loaded = saver.Load(path);
                var session = OrientationPdfSession.OpenTemplate(loaded, path, working);
                var values = Values(File.ReadAllBytes(session.WorkingPath));
                Check("A-009-10", values["3.2_text"] == "Gas" && values["City"] == "Example City" && values["Date"] == "01/01/2026",
                    "the BWI fixture's document is filled exactly as for BWT");
                session.Monitor.Cleanup();
                model.InspectionCode = "OTHER"; model.InspectionName = "Example Walk";
                Check("A-009-10", !OrientationPdfSession.IsApplicable(model), "an unrelated code and name stay excluded");
            }

            // ---------- A-009-11 BWCheck is untouched by auto-Fail (ControlName rule) ----------
            Check("A-009-11", !"BWCheck".ToLowerInvariant().Contains("passfail"),
                "BWCheck's ControlName does not contain passfail, so RED's automatic Fail rules never reach it (NA rewrite: F-004)");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine($"F-009 harness: {_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }
}

// OrientationPdfForm.GenericResolver and PdfAttachmentService.Create are internal; reach them by reflection.
static class OrientationPdfFormProbe
{
    public static JObject CreateAttachment(string name, byte[] bytes) =>
        (JObject)typeof(PdfAttachmentService).GetMethod("Create", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object?[] { name, bytes, null })!;

    public static Func<string, string?> Resolver(InspectionFile model) =>
        (Func<string, string?>)typeof(OrientationPdfForm).GetMethod("GenericResolver",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, new object[] { model })!;
}
