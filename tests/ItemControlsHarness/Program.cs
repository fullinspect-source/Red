// F-004 item controls harness (program/features/F-004-item-controls/ACCEPTANCE.md).
//   dotnet run                          fixture checks A-004-01..27 in a fresh temp sandbox
//   dotnet run -- --corpus <SBX> [report.md]
//                                       SPEC §10 V1-V3 and §11 shapes over a sandbox COPY of Inspections
// Fixtures are synthetic (job 1234567, initials XX, 100 Example St, placeholder fee ciphertexts). Corpus reports name
// files by folder and index only and print value shapes, never names, addresses, phone numbers or comment text.
using System.Globalization;
using System.Text;
using InspectionEditor.Models;
using InspectionEditor.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

if (args.Length >= 2 && args[0] == "--corpus")
    return Corpus.Run(args[1], args.Length > 2 ? args[2] : null);
if (args.Length >= 2 && args[0] == "--ui-fixture")
{
    // One synthetic report with every control, for clicking through RED's panes on a sandbox copy.
    Json.Write(args[1], Fixtures.UiDocument());
    Console.WriteLine("wrote UI fixture");
    return 0;
}
return Fixtures.Run();

static class Json
{
    public static readonly UTF8Encoding Utf8 = new(false);
    public static JObject ParseFile(string path) => InspectionFileReader.ParseDocument(File.ReadAllBytes(path), out _);
    public static string Compact(JToken token) => token.ToString(Formatting.None);
    public static void Write(string path, JObject document) => File.WriteAllBytes(path, Utf8.GetBytes(Compact(document)));

    public static IEnumerable<JObject> Items(JObject document) =>
        (document["Sections"] as JArray ?? new JArray()).OfType<JObject>()
            .SelectMany(s => (s["Items"] as JArray ?? new JArray()).OfType<JObject>());

    public static JObject Item(JObject document, string number) => Items(document).First(i => (string?)i["Number"] == number);

    /// <summary>JSON paths (keys and indices only) where two documents differ.</summary>
    public static List<string> Diff(JToken a, JToken b, string path = "$")
    {
        var differences = new List<string>();
        if (a.Type != b.Type) { differences.Add(path); return differences; }
        switch (a)
        {
            case JObject oa:
                var ob = (JObject)b;
                foreach (var p in oa.Properties())
                    differences.AddRange(ob.ContainsKey(p.Name) ? Diff(p.Value, ob[p.Name]!, path + "." + p.Name) : new() { path + "." + p.Name + " (removed)" });
                foreach (var p in ob.Properties())
                    if (!oa.ContainsKey(p.Name)) differences.Add(path + "." + p.Name + " (added)");
                break;
            case JArray aa:
                var ab = (JArray)b;
                if (aa.Count != ab.Count) { differences.Add(path + " (length)"); break; }
                for (int i = 0; i < aa.Count; i++) differences.AddRange(Diff(aa[i], ab[i], path + "[" + i + "]"));
                break;
            default:
                if (Compact(a) != Compact(b)) differences.Add(path);
                break;
        }
        return differences;
    }

    public static readonly string[] FeeKeys =
        { "InspectionFee_Secure", "InspectorOtherFee1_Secure", "InspectorOtherFee2_Secure", "OtherFeeDescription1", "OtherFeeDescription2" };
}

static class Fixture
{
    public static string FakeJpeg(int seed) => Convert.ToBase64String(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, (byte)seed, 1, 2, 3, 4, 5, 6, 7 });

    // INSPECT-local item keys in INSPECT's order.
    public static JObject Item(int itemId, string number, string name, string control, object? value = null, string? comments = null,
        string[]? valueList = null, int pictures = 0, string group = "", bool hide = false, bool required = false,
        bool hidePictures = false, bool hideComments = false, string? template = null, string? buttonText = null)
    {
        var pics = new JArray();
        for (int p = 0; p < pictures; p++)
            pics.Add(new JObject { ["Path"] = null, ["Caption"] = "Picture", ["Description"] = null, ["Image"] = FakeJpeg(itemId % 200 + p) });
        return new JObject
        {
            ["ItemId"] = itemId,
            ["Name"] = name,
            ["Number"] = number,
            ["ControlName"] = control,
            ["ControlFormatString"] = null,
            ["ItemGroupId"] = group,
            ["ItemGroupControlName"] = group.Length > 0 ? "SlabExtMeasures" : null,
            ["PictureSizeId"] = 0,
            ["IsCopied"] = false,
            ["Hide"] = hide,
            ["MaxIncidents"] = null,
            ["SortOrder"] = 1,
            ["Required"] = required,
            ["AlwaysPrint"] = false,
            ["OnlyPrint"] = "",
            ["Template"] = template,
            ["ButtonText"] = buttonText,
            ["HideCommentsButton"] = hideComments,
            ["HidePicturesButton"] = hidePictures,
            ["HideAddButton"] = false,
            ["CommentList"] = new JArray(),
            ["ValueList"] = valueList == null ? new JArray() : new JArray(valueList),
            ["IsPictureRequired"] = false,
            ["Pictures"] = pics,
            ["DisplayLabel"] = $"{number} {name}",
            ["Value"] = value == null ? JValue.CreateNull() : JToken.FromObject(value),
            ["ResultSortOrder"] = 0,
            ["Comments"] = comments,
            ["Incidents"] = null
        };
    }

    public static JObject Document(int formId, bool reinspection, params (string Number, string Name, string Control, JObject[] Items)[] sections)
    {
        var sectionArray = new JArray();
        int sectionId = 900;
        foreach (var s in sections)
            sectionArray.Add(new JObject
            {
                ["SectionId"] = ++sectionId,
                ["Name"] = s.Name,
                ["ControlName"] = s.Control,
                ["Number"] = s.Number,
                ["Items"] = new JArray(s.Items),
                ["SortOrder"] = sectionId - 900
            });
        return new JObject
        {
            ["InspectVersion"] = "",
            ["WorkOrderId"] = "00000000-0000-0000-0000-000000000004",
            ["InspectionName"] = "100 Example St, Example Project",
            ["InspectionNumber"] = "1234567-XX-1-XX",
            ["InspectionCode"] = "XX",
            ["FormId"] = formId,
            ["ServiceId"] = 61,
            ["Address"] = "100 Example St",
            ["City"] = "Example City",
            ["State"] = "TX",
            ["IsReinspection"] = reinspection,
            ["InspectionFee_Secure"] = "PLACEHOLDER-FEE-1",
            ["InspectorOtherFee1_Secure"] = "PLACEHOLDER-FEE-2",
            ["InspectorOtherFee2_Secure"] = "PLACEHOLDER-FEE-3",
            ["OtherFeeDescription1"] = "",
            ["OtherFeeDescription2"] = null,
            ["Sections"] = sectionArray,
            ["Events"] = new JArray(),
            ["Attachments"] = new JArray()
        };
    }
}

static class Fixtures
{
    private static int _passed, _failed;
    private static string _root = "";

    private static void Check(string id, bool ok, string what)
    {
        if (ok) _passed++; else _failed++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {id} {what}");
    }

    public static JObject UiDocument() => Fixture.Document(27, true,
        ("1", "Checks", "PassFailNaNi", new[]
        {
            Fixture.Item(9101, "1.1", "Legacy NA item", "PassFailNaNi", "NA"),
            Fixture.Item(9102, "1.2", "Footing failed earlier", "PassFailNaNi", "Fail", "Gap at trim"),
            Fixture.Item(9103, "1.3", "Pass fail ni", "PassFailNi"),
            Fixture.Item(9104, "1.4", "Plain pass fail", "PassFail"),
            Fixture.Item(9105, "1.5", "Yes no legacy", "YesNo", "N/A"),
            Fixture.Item(9106, "1.6", "Yes no na ni legacy", "YesNoNaNi", "Yes"),
            Fixture.Item(9107, "1.7", "Walk check", "BWCheck", "NA"),
            Fixture.Item(9108, "1.8", "Flags hidden", "PassFailNaNi", hidePictures: true, hideComments: true),
        }),
        ("2", "Cylinders", "Simple", new[]
        {
            Fixture.Item(1615, "2.0", "Test cylinders included", "CPRYesNo", required: true),
            Fixture.Item(9201, "2.1", "Pump truck", "YesNo"),
            Fixture.Item(9202, "2.2", "Walk item", "BWCheck"),
        }),
        ("3", "Values", "Simple", new[]
        {
            Fixture.Item(9301, "3.1", "Customer Phone", "Text"),
            Fixture.Item(9302, "3.2", "Lot notes", "TextNaNi"),
            Fixture.Item(9303, "3.3", "Narrative", "Memo", required: true),
            Fixture.Item(9304, "3.4", "Insulation", "Lookup", "R-49", valueList: new[] { "R-30", "R-38" }),
            Fixture.Item(9305, "3.5", "Furnace", "LookupNaNi", valueList: new[] { "Gas", "Electric" }),
            Fixture.Item(9306, "3.6", "Readiness", "HomeReadiness"),
            Fixture.Item(9307, "3.7", "SUPERINTENDENT", "SearchLookup", valueList: new[] { "Super A" }),
            Fixture.Item(9308, "3.8", "Slump", "NumberPad"),
            Fixture.Item(9309, "3.9", "Depth", "NumberPadNaNi"),
            Fixture.Item(9310, "3.10", "Arrival", "Time"),
            Fixture.Item(9311, "3.11", "Pour date", "Date"),
            Fixture.Item(9312, "3.12", "Site picture", "PictureUpload", ""),
            Fixture.Item(9313, "3.13", "Survey file", "DocumentUpload", buttonText: ""),
            Fixture.Item(9314, "3.14", "Orientation form", "DocumentButton", "", buttonText: "Open Form", template: "Example.pdf"),
            Fixture.Item(9315, "3.15", "Info button", "Button", buttonText: "Info"),
            Fixture.Item(9316, "3.16", "Gauge", "FancyGauge", required: true),
        }),
        ("4", "Truck log", "Simple", new[]
        {
            Fixture.Item(9401, "4.1.A", "Truck 1 Arrival", "PassFail", "08:25 AM", group: "1"),
            Fixture.Item(9402, "4.1.G", "Truck 1 Gallons", "PassFail", "10", group: "1"),
        }));

    private static SurgicalSaveService Saver(string work) =>
        new(new FailedSaveRecoveryService(Path.Combine(work, "Recovery")), null, Path.Combine(work, "VerifiedSaves"));

    private static string WriteFixture(string name, JObject document)
    {
        string work = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.Combine(work, "MyList"));
        string path = Path.Combine(work, "MyList", "1234567-XX-1-XX.ins");
        Json.Write(path, document);
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc));
        return path;
    }

    private static Item ModelItem(InspectionFile model, string number) =>
        model.Sections.SelectMany(s => s.Items).First(i => i.Number == number);

    private static Section ModelSection(InspectionFile model, string number) =>
        model.Sections.First(s => s.Items.Any(i => i.Number == number));

    private static string? FileValue(string path, string number) => (string?)Json.Item(Json.ParseFile(path), number)["Value"];

    private static bool Unchanged(string path, byte[] before, DateTime stamp) =>
        File.ReadAllBytes(path).SequenceEqual(before) && File.GetLastWriteTimeUtc(path) == stamp;

    public static int Run()
    {
        _root = Path.Combine(Path.GetTempPath(), "red-f004-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        try
        {
            Choices();
            NaRewrite();
            Memo();
            LookupsAndSearchLookup();
            NumberPad();
            TimeAndDate();
            Uploads();
            UnknownAndVisibility();
            Copies();
            ItemFixed();
            AllLabels();
            AutoFail();
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine("FAIL harness exception: " + ex);
        }
        finally
        {
            try { Directory.Delete(_root, true); } catch { }
        }
        Console.WriteLine($"{_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    // A-004-01, -02, -03, -05, -16, -21, -22
    private static void Choices()
    {
        var pfNaNi = ItemControls.Resolve("PassFailNaNi");
        Check("A-004-01", pfNaNi.Choices!.SequenceEqual(new[] { "Pass", "Fail", "NI" }), "PassFailNaNi offers Pass, Fail, NI and no N/A");
        var item = new Item { ControlName = "PassFailNaNi" };
        var stored = new List<string?>();
        item.Value = ItemControls.NextChoiceValue(item, "Pass"); stored.Add((string?)item.Value);
        item.Value = ItemControls.NextChoiceValue(item, "Pass"); stored.Add((string?)item.Value);
        item.Value = ItemControls.NextChoiceValue(item, "NI"); stored.Add((string?)item.Value);
        Check("A-004-01", stored.SequenceEqual(new string?[] { "Pass", null, "NI" }), "Pass, Pass again, NI store \"Pass\", null, \"NI\"");

        string path = WriteFixture("a01", Fixture.Document(20, false, ("1", "Checks", "PassFailNaNi", new[]
        {
            Fixture.Item(101, "1.1", "Footing", "PassFailNaNi"),
            Fixture.Item(102, "1.2", "Beams", "YesNoNaNi"),
            Fixture.Item(103, "1.3", "Legacy", "YesNoNaNi", "Yes"),
            Fixture.Item(104, "1.4", "Walk item", "BWCheck"),
            Fixture.Item(105, "1.5", "Plain", "PassFail"),
        })));
        var saver = Saver(Path.GetDirectoryName(Path.GetDirectoryName(path))!);
        var model = saver.Load(path);
        var a = ModelItem(model, "1.1");
        a.Value = ItemControls.NextChoiceValue(a, "Pass");
        saver.Save(model);
        Check("A-004-01", FileValue(path, "1.1") == "Pass", "the file stores \"Pass\"");
        a.Value = ItemControls.NextChoiceValue(a, "Pass");
        saver.Save(model);
        Check("A-004-01", Json.Item(Json.ParseFile(path), "1.1")["Value"]!.Type == JTokenType.Null, "clicking Pass again stores null");
        a.Value = ItemControls.NextChoiceValue(a, "NI");
        saver.Save(model);
        Check("A-004-01", FileValue(path, "1.1") == "NI", "NI stores \"NI\"");

        var yesNoNaNi = ItemControls.Resolve("YesNoNaNi");
        var b = ModelItem(model, "1.2");
        b.Value = ItemControls.NextChoiceValue(b, yesNoNaNi.Choices![0]);
        saver.Save(model);
        Check("A-004-02", yesNoNaNi.Choices.SequenceEqual(new[] { "Pass", "Fail", "NI" }) && FileValue(path, "1.2") == "Pass",
            "YesNoNaNi offers Pass, Fail, NI; the first choice stores \"Pass\"");
        Check("A-004-03", (string?)ModelItem(model, "1.3").Value == "Yes" && FileValue(path, "1.3") == "Yes", "legacy YesNoNaNi \"Yes\" kept");

        var bw = ItemControls.Resolve("BWCheck");
        var c = ModelItem(model, "1.4");
        c.Value = ItemControls.NextChoiceValue(c, "NA");
        saver.Save(model);
        Check("A-004-05", bw.Choices!.SequenceEqual(new[] { "Discussed", "NA" }) && FileValue(path, "1.4") == "NA", "BWCheck NA stores \"NA\"");
        Check("A-004-21", !bw.HasNiToggle && !bw.RewritesNaToNi, "BWCheck has no NI toggle and is not rewritten");

        var plain = ItemControls.Resolve("PassFail");
        var d = ModelItem(model, "1.5");
        d.Value = ItemControls.NextChoiceValue(d, "Fail");
        saver.Save(model);
        Check("A-004-16", plain.Choices!.SequenceEqual(new[] { "Pass", "Fail" }) && FileValue(path, "1.5") == "Fail",
            "standalone PassFail offers Pass and Fail only and stores \"Fail\"");

        Check("A-004-22", ItemControls.Resolve("YesNo").Choices!.SequenceEqual(new[] { "Yes", "No" }) &&
            yesNoNaNi.Choices.SequenceEqual(new[] { "Pass", "Fail", "NI" }) && ItemControls.Resolve("YesNoNi").Choices!.SequenceEqual(new[] { "Pass", "Fail", "NI" }),
            "YesNo offers Yes, No; YesNoNi/YesNoNaNi offer Pass, Fail, NI (no N/A)");
        Check("A-004-22", ItemControls.Resolve("CPRYesNo").Choices!.SequenceEqual(new[] { "Yes", "No" }) &&
            ItemControls.Resolve("PassFailNi").Choices!.SequenceEqual(new[] { "Pass", "Fail", "NI" }), "CPRYesNo Yes/No; PassFailNi Pass/Fail/NI");

        // The open of A-004-03 alone wrote nothing: reopen, change nothing, save.
        byte[] before = File.ReadAllBytes(path);
        DateTime stamp = File.GetLastWriteTimeUtc(path);
        var reopened = Saver(Path.GetDirectoryName(Path.GetDirectoryName(path))!);
        reopened.Save(reopened.Load(path));
        Check("A-004-03", Unchanged(path, before, stamp), "opening and closing without edits writes nothing");
    }

    // A-004-04, A-004-21 (open), SPEC V1 scope
    private static void NaRewrite()
    {
        string path = WriteFixture("a04", Fixture.Document(20, false,
            ("1", "Checks", "Simple", new[]
            {
                Fixture.Item(201, "1.1", "Item A", "PassFailNaNi", "NA"),
                Fixture.Item(202, "1.2", "Item B", "YesNo", "N/A"),
                Fixture.Item(203, "1.3", "Item C", "BWCheck", "NA"),
                Fixture.Item(204, "1.4", "Item D", "Text"),
                Fixture.Item(205, "1.5", "Lower case", "TextNaNi", "na"),
                Fixture.Item(206, "1.6", "Plain", "PassFail", "NA"),
                Fixture.Item(207, "1.7", "Gauge", "FancyGauge", "NA"),
                Fixture.Item(208, "1.8", "Signature", "Signature", "NA"),
                Fixture.Item(209, "1.9", "Hidden", "TextNi", "NA", hide: true),
                Fixture.Item(210, "1.10", "Grid cell", "NumberPad", "NA", group: "1"),
                Fixture.Item(211, "1.11", "Yes no", "YesNo", "NA"),
                Fixture.Item(212, "1.12", "Text", "Text", "NA"),
            })));
        string work = Path.GetDirectoryName(Path.GetDirectoryName(path))!;
        byte[] before = File.ReadAllBytes(path);
        DateTime stamp = File.GetLastWriteTimeUtc(path);

        var saver = Saver(work);
        var model = saver.Load(path);
        Check("A-004-04", (string?)ModelItem(model, "1.1").Value == "NI", "item A (PassFailNaNi \"NA\") shows NI after open");
        Check("A-004-21", (string?)ModelItem(model, "1.3").Value == "NA", "BWCheck \"NA\" still shows NA after open");
        Check("V1", (string?)ModelItem(model, "1.11").Value == "NI" && (string?)ModelItem(model, "1.12").Value == "NI",
            "YesNo and Text \"NA\" become NI (C1: INSPECT writes NI on sets without NI)");
        Check("V1", (string?)ModelItem(model, "1.2").Value == "N/A" && (string?)ModelItem(model, "1.5").Value == "na",
            "\"N/A\" and \"na\" are kept (ordinal, exact \"NA\" only)");
        Check("V1", new[] { "1.6", "1.7", "1.8", "1.9", "1.10" }.All(n => (string?)ModelItem(model, n).Value == "NA"),
            "plain PassFail, unknown, Signature, hidden and grid items keep \"NA\"");
        Check("V1", saver.HasPendingNaRewrites, "the rewrite is pending");
        saver.Save(model);
        Check("A-004-04", Unchanged(path, before, stamp), "closed without edits: the file is unchanged (F-002 A-002-01)");

        // SPEC V1: Submit carries the NI even when no save wrote it (F-013 projection).
        var projected = InspectionFileReader.ParseDocument(saver.ProjectCurrent(model).ToUtf8Bytes(), out _);
        Check("V1", (string?)Json.Item(projected, "1.1")["Value"] == "NI" && (string?)Json.Item(projected, "1.3")["Value"] == "NA",
            "the Submit projection carries NI for item A and keeps BWCheck NA");

        // Save As Copy also writes the model as shown.
        string copy = Path.Combine(work, "copy.ins");
        saver.SaveCopy(model, copy);
        Check("V1", FileValue(copy, "1.1") == "NI" && Unchanged(path, before, stamp), "Save As Copy writes NI; the report is untouched");

        var reopened = Saver(work);
        var again = reopened.Load(path);
        EditorEditService.SetValue(again, ModelItem(again, "1.4"), "Edited");
        reopened.Save(again);
        var after = Json.ParseFile(path);
        Check("A-004-04", (string?)Json.Item(after, "1.1")["Value"] == "NI" && (string?)Json.Item(after, "1.2")["Value"] == "N/A" &&
            (string?)Json.Item(after, "1.3")["Value"] == "NA", "after editing only item D: A stores NI, B N/A, C NA");
        var original = InspectionFileReader.ParseDocument(before, out _);
        var changedItems = Json.Diff(original, after).Select(p => p.Replace("$.Sections[0].Items", "")).ToList();
        Check("A-004-04", changedItems.SequenceEqual(new[] { "[0].Value", "[3].Value", "[10].Value", "[11].Value" }),
            "only Value changed, on A, D and the two other NA items: " + string.Join(", ", changedItems));
        Check("Q-21", Json.FeeKeys.All(k => Json.Compact(original[k]!) == Json.Compact(after[k]!)), "fee fields byte for byte");
        Check("V1", !reopened.HasPendingNaRewrites, "nothing pending after the save that wrote NI");
    }

    // A-004-06, -07
    private static void Memo()
    {
        string path = WriteFixture("a06", Fixture.Document(44, false, ("1", "Narrative", "Simple", new[]
        {
            Fixture.Item(301, "1.1", "Observations", "Memo"),
            Fixture.Item(302, "1.2", "Old memo", "Memo", "ok", "Earlier text"),
        })));
        string work = Path.GetDirectoryName(Path.GetDirectoryName(path))!;
        var saver = Saver(work);
        var model = saver.Load(path);
        var memo = ModelItem(model, "1.1");
        EditorEditService.SetComment(model, memo, "Crack at north wall");
        saver.Save(model);
        var after = Json.Item(Json.ParseFile(path), "1.1");
        Check("A-004-06", (string?)after["Comments"] == "Crack at north wall" && (string?)after["Value"] == "ok",
            "typing sets Comments and Value \"ok\"");
        EditorEditService.SetComment(model, memo, "");
        saver.Save(model);
        after = Json.Item(Json.ParseFile(path), "1.1");
        Check("A-004-06", after["Value"]!.Type == JTokenType.Null && (string?)after["Comments"] == "", "deleting all text stores Value null");
        Check("A-004-06", FileValue(path, "1.2") == "ok", "an untouched memo keeps its Value");
        Check("A-004-07", ItemControls.Resolve("Memo").MaxLength == 1000 && ItemControls.MemoMaxLength == 1000,
            "Memo editors are limited to 1000 characters (the TextBox MaxLength; paste is checked in the app)");
        Check("SPEC §3", ItemControls.Resolve("Text").MaxLength == 100 && ItemControls.Resolve("TextNaNi").MaxLength == 100,
            "Text editors are limited to 100 characters");

        // A-004-08: TextControl::UsePhoneMask, RegEx "\(\d{3}\) [0-9A-Z]{3}-[0-9A-Z]{4}"; stored with its literals.
        var phone = new Item { ControlName = "Text", Name = "Customer Phone" };
        string? typed = "";
        foreach (char key in "5550100100") typed = ItemControls.ApplyPhoneMask(typed + key);
        Check("A-004-08", ItemControls.UsesPhoneMask(phone) && typed == "(555) 010-0100", $"typing 5550100100 stores \"{typed}\"");
        Check("A-004-08", ItemControls.UsesPhoneMask(new Item { ControlName = "Text", Name = "Sales Agent Phone" }) &&
            !ItemControls.UsesPhoneMask(new Item { ControlName = "Text", Name = "customer phone" }) &&
            !ItemControls.UsesPhoneMask(new Item { ControlName = "Lookup", Name = "Customer Phone" }),
            "exact names on Text controls only");
        Check("A-004-08", ItemControls.ApplyPhoneMask("555 flo wers 99") == "(555) FLO-WERS" && ItemControls.ApplyPhoneMask("(55") == "(55" &&
            ItemControls.ApplyPhoneMask("(555) 0") == "(555) 0" && ItemControls.ApplyPhoneMask("abc") == "",
            "letters only after the area code, upper-cased; partial entries stay editable");
    }

    // A-004-09, -10, HomeReadiness
    private static void LookupsAndSearchLookup()
    {
        string path = WriteFixture("a09", Fixture.Document(91, false, ("1", "Data", "Simple", new[]
        {
            Fixture.Item(401, "1.1", "Insulation", "Lookup", "R-49", valueList: new[] { "R-30", "R-38" }),
            Fixture.Item(402, "1.2", "Other", "Text"),
            Fixture.Item(403, "1.3", "SUPERINTENDENT", "SearchLookup", null, valueList: new[] { "Super A" }),
            Fixture.Item(404, "1.4", "Readiness", "HomeReadiness"),
        })));
        string work = Path.GetDirectoryName(Path.GetDirectoryName(path))!;
        var saver = Saver(work);
        var model = saver.Load(path);
        EditorEditService.SetValue(model, ModelItem(model, "1.2"), "x");
        saver.Save(model);
        Check("A-004-09", FileValue(path, "1.1") == "R-49" && (string?)ModelItem(model, "1.1").Value == "R-49",
            "a Lookup value outside its list is kept and shown");

        var super = ModelItem(model, "1.3");
        Check("A-004-10", ItemControls.For(super).Kind == ItemControlKind.SearchLookup && ItemControls.IsSuperintendentItem(super),
            "SUPERINTENDENT is a SearchLookup (editable, owner Q-16)");
        ItemControls.AddSearchLookupValue(super, "Super B");
        saver.Save(model);
        var after = Json.Item(Json.ParseFile(path), "1.3");
        Check("A-004-10", (string?)after["Value"] == "Super B" &&
            ((JArray)after["ValueList"]!).Select(v => (string?)v).SequenceEqual(new[] { "Super A", "Super B" }),
            "Add New stores \"Super B\" and grows ValueList");
        Check("A-004-10", ItemControls.SearchLookupChoices(new Item { ControlName = "SearchLookup", ValueList = new() { "Zed", "Abe" }, Value = "Moe" })
            .SequenceEqual(new[] { "Abe", "Zed", "Moe" }), "the shown list is sorted, with a missing current value appended");
        Check("SPEC §3", ItemControls.HomeReadinessChoices.SequenceEqual(Enumerable.Range(1, 10).Select(n => n.ToString())),
            "HomeReadiness offers 1 to 10");
    }

    // A-004-11, -12
    private static void NumberPad()
    {
        string? value = "";
        var steps = new List<string?>();
        foreach (string key in new[] { "4", "¾", "2" })
        {
            ItemControls.PressNumberPadKey(value, key, out value);
            steps.Add(value);
        }
        Check("A-004-11", steps.SequenceEqual(new[] { "4", "4.75", "4.75" }), "4, ¾, 2 give \"4\", \"4.75\", \"4.75\"");
        ItemControls.PressNumberPadKey("4.25", "½", out string? half);
        ItemControls.PressNumberPadKey("", "¼", out string? quarter);
        ItemControls.PressNumberPadKey("12", "5", out string? digits);
        ItemControls.PressNumberPadKey("NI", "7", out string? overNi);
        var cleared = ItemControls.PressNumberPadKey("25.5", "CLEAR", out string? clearedValue);
        Check("SPEC §4", half == "4.5" && quarter == "0.25" && digits == "125" && overNi == "7" && cleared == NumberPadOutcome.Changed && clearedValue == "",
            "N1/N2/N3/N4: ½ on 4.25 → 4.5, ¼ on empty → 0.25, 12+5 → 125, NI+7 → 7, CLEAR → \"\"");
        var big = ItemControls.PressNumberPadKey("10000", "0", out string? bigValue);
        Check("A-004-12", big == NumberPadOutcome.TooLarge && bigValue == "10000", "entering 100000 is refused and the value is unchanged");
        Check("A-004-12", ItemControls.NumberTooLargeMessage == "Value cannot be greater than 99999.00" && ItemControls.NumberTooLargeCaption == "Value Too Large",
            "message and caption match INSPECT");
        Check("A-004-12", !ItemControls.IsAcceptableNumberText("100000", out bool tooLarge) && tooLarge && ItemControls.IsAcceptableNumberText("99999", out _),
            "the keyboard editor refuses 100000 and accepts 99999");
        Check("SPEC §4", ItemControls.NormalizeTypedNumber("4.50") == "4.5" && ItemControls.NormalizeTypedNumber("4.") == "4" &&
            ItemControls.NormalizeTypedNumber(".") == "" && ItemControls.FormatNumberDisplay("25.5") == "25.50",
            "N7 plain decimal storage; N6 N2 display");
        Check("SPEC §4", !ItemControls.IsAcceptableNumberText("1,000", out _) && !ItemControls.IsAcceptableNumberText("4.5.1", out _) &&
            !ItemControls.IsAcceptableNumberText("-3", out _), "no separators, second points or signs");
    }

    // A-004-13
    private static void TimeAndDate()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            string time = ItemControls.FormatTime(new DateTime(2026, 3, 3, 5, 0, 0));
            string date = ItemControls.FormatDate(new DateTime(2026, 3, 3));
            Check("A-004-13", time == "5:00 AM" && date == "3/3/2026", $"non-US region stores \"{time}\" and \"{date}\"");
            Check("A-004-13", ItemControls.TryParseTime("5:00 PM", out var pm) && pm.Hour == 17 &&
                ItemControls.TryParseDate("3/4/2026", out var d) && d.Month == 3 && d.Day == 4, "stored values are read back as en-US");
        }
        finally { CultureInfo.CurrentCulture = saved; }
    }

    // A-004-14, DocumentUpload (owner Q-13 default)
    private static void Uploads()
    {
        string path = WriteFixture("a14", Fixture.Document(300, false, ("1", "Survey", "Simple", new[]
        {
            Fixture.Item(501, "1.1", "Site photo", "PictureUpload", ""),
            Fixture.Item(502, "1.2", "Survey file", "DocumentUpload", null, template: null, buttonText: ""),
        })));
        string work = Path.GetDirectoryName(Path.GetDirectoryName(path))!;
        var saver = Saver(work);
        var model = saver.Load(path);
        var upload = ModelItem(model, "1.1");
        Check("A-004-14", ItemControls.PictureUploadCaption(upload) == "Upload Picture", "button reads \"Upload Picture\"");
        upload.Pictures.Add(new Picture { Data = Fixture.FakeJpeg(9), Title = "Site photo", Comment = "" });
        saver.Save(model);
        Check("A-004-14", FileValue(path, "1.1") == "[Uploaded]" && ItemControls.PictureUploadCaption(upload) == "Change Picture",
            "one picture stores \"[Uploaded]\" and the button reads \"Change Picture\"");
        upload.Pictures.Clear();
        saver.Save(model);
        Check("A-004-14", FileValue(path, "1.1") == "", "removing the picture stores \"\"");

        var doc = ModelItem(model, "1.2");
        Check("Q-13", ItemControls.DocumentUploadCaption(doc) == "Upload File", "an empty ButtonText shows \"Upload File\"");
        byte[] bytes = Encoding.UTF8.GetBytes("line one\nline two");
        ItemControls.StoreDocumentUpload(doc, "survey.txt", bytes);
        saver.Save(model);
        var after = Json.Item(Json.ParseFile(path), "1.2");
        Check("Q-13", (string?)after["Value"] == "line one\nline two" && (string?)after["Template"] == "survey.txt" &&
            ItemControls.DocumentUploadCaption(doc) == "Change File", "file text in Value, file name in Template, then \"Change File\"");
        var section = model.Sections[0];
        var copy = SurgicalSaveService.InsertCopy(model, section, doc);
        saver.Save(model);
        var copyJson = Json.Items(Json.ParseFile(path)).Where(i => (string?)i["Number"] == "1.2").ElementAt(1);
        Check("A4", copyJson["Template"]!.Type == JTokenType.Null && copyJson["Value"]!.Type == JTokenType.Null,
            "a copy starts without Template or Value (Item.Clone)");
    }

    // A-004-15, -17
    private static void UnknownAndVisibility()
    {
        var gauge = new Item { ControlName = "FancyGauge", Required = true, Name = "Gauge" };
        var control = ItemControls.For(gauge);
        Check("A-004-15", control.Kind == ItemControlKind.Unsupported && ItemControls.UnsupportedBadge(gauge) == "Unsupported control: FancyGauge" &&
            ChecklistItemVisibility.ShouldShow(gauge), "unknown control: visible, badge \"Unsupported control: FancyGauge\", text editor");
        Check("A-004-15", ItemControls.Resolve("passfailnani").Kind == ItemControlKind.Unsupported,
            "names match exactly, as INSPECT's type lookup does");
        var flags = new Item { ControlName = "Text", HidePicturesButton = true, HideCommentsButton = true };
        Check("A-004-17", ChecklistItemVisibility.ShouldShow(flags), "both Hide*Button flags and no photo: row visible");
    }

    // A-004-18, K4
    private static void Copies()
    {
        string path = WriteFixture("a18", Fixture.Document(20, false,
            ("2", "Framing", "Simple", new[]
            {
                Fixture.Item(601, "2.3", "Beam", "PassFailNaNi"),
                Fixture.Item(602, "2.4", "Post", "PassFailNaNi"),
            }),
            ("8", "Measures", "Simple", new[]
            {
                Fixture.Item(603, "8.1", "TOF to TOG", "NumberPad", group: "1"),
                Fixture.Item(604, "8.2", "TOF to BOB", "NumberPad", group: "1"),
            })));
        var saver = Saver(Path.GetDirectoryName(Path.GetDirectoryName(path))!);
        var model = saver.Load(path);
        var section = model.Sections[0];
        var source = ModelItem(model, "2.3");
        Check("A-004-18", !ItemControls.CanRemove(section, source), "Remove is not offered on 2.3 without copies");
        var copy = SurgicalSaveService.InsertCopy(model, section, source);
        Check("A-004-18", ItemControls.CanRemove(section, source) && ItemControls.CanRemove(section, copy), "after a copy, Remove is offered on both rows");
        var grid = ModelItem(model, "8.1");
        Check("K4", !ItemControls.CanCopy(grid) && !ItemControls.CanRemove(model.Sections[1], grid) && ItemControls.CanCopy(source),
            "no Copy or Remove inside item-group grids");
        var truckCell = new Item { ControlName = "PassFail", Value = "08:25 AM", ExtensionData = new() { ["ItemGroupId"] = "1" } };
        Check("C1/C2", ItemControls.TryValueForControl(new Item { ControlName = "YesNo" }, "yes", out string yes) && yes == "Yes" &&
            !ItemControls.TryValueForControl(new Item { ControlName = "YesNo" }, "N/A", out _) &&
            !ItemControls.TryValueForControl(new Item { ControlName = "YesNoNaNi" }, "Yes", out _) &&
            !ItemControls.TryValueForControl(new Item { ControlName = "Lookup" }, "NA", out _) &&
            ItemControls.TryValueForControl(new Item { ControlName = "BWCheck" }, "na", out string na) && na == "NA" &&
            !ItemControls.TryValueForControl(new Item { ControlName = "HomeReadiness" }, "Pass", out _) &&
            ItemControls.TryValueForControl(new Item { ControlName = "HomeReadiness" }, "7", out _) &&
            !ItemControls.TryValueForControl(new Item { ControlName = "NumberPad" }, "NI", out _) &&
            ItemControls.TryValueForControl(new Item { ControlName = "NumberPadNaNi" }, "ni", out string ni) && ni == "NI" &&
            !ItemControls.TryValueForControl(new Item { ControlName = "Date" }, "SN 12345", out _) &&
            !ItemControls.TryValueForControl(new Item { ControlName = "Memo" }, "text", out _),
            "bulk writers (templates, AI, EC) store only values in the control's set");
        Check("F6", ItemControls.For(truckCell).Kind == ItemControlKind.Text && ItemControls.For(truckCell).Choices == null &&
            !ItemControls.TryValueForControl(new Item { ControlName = "PassFail" }, "NI", out _) && ItemControls.TryValueForControl(truckCell, "08:30 AM", out _),
            "a TruckPlacement cell (ControlName PassFail in a group) is a text row until F-005, not Pass/Fail buttons");
    }

    // A-004-19
    private static void ItemFixed()
    {
        string path = WriteFixture("a19", Fixture.Document(20, true, ("1", "Checks", "Simple", new[]
        {
            Fixture.Item(701, "1.1", "Footing", "PassFailNaNi", "Fail", "Gap at trim", pictures: 1),
            Fixture.Item(702, "1.2", "Walk", "BWCheck", null, "Note"),
            Fixture.Item(703, "1.3", "Clean", "PassFailNaNi", "Fail"),
        })));
        var saver = Saver(Path.GetDirectoryName(Path.GetDirectoryName(path))!);
        var model = saver.Load(path);
        var item = ModelItem(model, "1.1");
        Check("A-004-19", ItemControls.IsReinspection(model) && ItemControls.AsksItemFixed(model, item, "Pass") &&
            ItemControls.AsksItemFixed(model, ModelItem(model, "1.2"), "Discussed") && !ItemControls.AsksItemFixed(model, item, "NI") &&
            !ItemControls.AsksItemFixed(model, ModelItem(model, "1.3"), "Pass"),
            "Pass/Discussed on a reinspection item with comments or pictures asks; NI or a bare item does not");
        Check("A-004-19", ItemControls.ItemFixedMessage == "If this item was fixed, would you like to remove the Comments and Pictures?" &&
            ItemControls.ItemFixedCaption == "Item Fixed Confirmation", "INSPECT's text and caption");
        item.Value = "Pass";
        ItemControls.ClearFixedItem(item);
        saver.Save(model);
        var after = Json.Item(Json.ParseFile(path), "1.1");
        Check("A-004-19", (string?)after["Value"] == "Pass" && (string?)after["Comments"] == "" && ((JArray)after["Pictures"]!).Count == 0,
            "Yes leaves Value \"Pass\", Comments \"\", Pictures []");
        var firstVisit = new InspectionFile { ExtensionData = new() { ["IsReinspection"] = new JValue(false) } };
        Check("A-004-19", !ItemControls.AsksItemFixed(firstVisit, new Item { ControlName = "PassFailNaNi", Comments = "x" }, "Pass"),
            "not on a first inspection");
    }

    // A-004-23, V4
    private static void AllLabels()
    {
        var cpr = new Section
        {
            ControlName = "Simple",
            Items = new()
            {
                new Item { ItemId = 1615, Number = "1.0", ControlName = "CPRYesNo" },
                new Item { ItemId = 2, Number = "1.1", ControlName = "YesNo", Value = "No" },
                new Item { ItemId = 3, Number = "1.2", ControlName = "Text" },
            }
        };
        var labels = ItemControls.SectionAllLabels(cpr, 3);
        Check("A-004-23", labels != null && labels.SequenceEqual(new[] { "YES", "NO" }), "FormId 27 section 1 (Simple) shows YES/NO");
        var mixed = new Section { Items = new() { new Item { ControlName = "YesNo" }, new Item { ControlName = "PassFailNaNi" }, new Item { ControlName = "BWCheck" } } };
        Check("SC3", ItemControls.SectionAllLabels(mixed, 3)!.SequenceEqual(new[] { "PASS", "FAIL", "NI" }) &&
            ItemControls.SectionAllLabels(mixed, 1) == null &&
            ItemControls.SectionAllLabels(new Section { Items = new() { new Item { ControlName = "Text" }, new Item { ControlName = "Memo" } } }, 2) == null,
            "PASS/FAIL/NI wins; none with one row or without checkbox items");
        var changed = ItemControls.ApplySectionAllLabel(cpr, "YES", item => item.ItemId == 1615);
        Check("V4", changed.Count == 1 && (string?)cpr.Items[1].Value == "Yes" && cpr.Items[0].Value == null && cpr.Items[2].Value == null,
            "YES sets column 1 on YesNo; item 1615 is skipped until F-008; Text untouched");
        ItemControls.ApplySectionAllLabel(cpr, "YES", item => item.ItemId == 1615);
        Check("V4", cpr.Items[1].Value == null, "applying a value an item already has clears it");
        var niSection = new Section
        {
            Items = new()
            {
                new Item { ControlName = "PassFailNaNi" }, new Item { ControlName = "PassFailNi" }, new Item { ControlName = "TextNaNi", Value = "NI" },
                new Item { ControlName = "YesNo" }, new Item { ControlName = "BWCheck" }, new Item { ControlName = "NumberPadNaNi" }
            }
        };
        ItemControls.ApplySectionAllLabel(niSection, "NI");
        Check("V4", (string?)niSection.Items[0].Value == "NI" && niSection.Items[1].Value == null && niSection.Items[2].Value == null &&
            niSection.Items[3].Value == null && niSection.Items[4].Value == null && (string?)niSection.Items[5].Value == "NI",
            "NI acts on INSPECT's list (PassFailNaNi, *NaNi toggles), not PassFailNi, YesNo or BWCheck");
        ItemControls.ApplySectionAllLabel(niSection, "NA");
        Check("V4", (string?)niSection.Items[4].Value == "NA" && (string?)niSection.Items[0].Value == "Fail" && (string?)niSection.Items[3].Value == "No",
            "NA picks column 2: BWCheck NA, PassFailNaNi Fail, YesNo No");
    }

    // A-004-24 .. A-004-27 (owner Q-15)
    private static void AutoFail()
    {
        Item I(string control, string? value, string? comments = null, int pictures = 0)
        {
            var item = new Item { ControlName = control, Value = value, Comments = comments };
            for (int p = 0; p < pictures; p++) item.Pictures.Add(new Picture { Data = Fixture.FakeJpeg(p) });
            return item;
        }

        var a = I("PassFailNaNi", "Pass"); var b = I("PassFailNaNi", "NI"); var c = I("PassFailNaNi", null); var d = I("PassFailNaNi", "Fail");
        var e = I("PassFailNi", "Pass"); var f = I("YesNoNaNi", "Pass"); var g = I("YesNo", "Yes");
        foreach (var item in new[] { a, b, c, d, f, g }) ItemAutoFailRules.ApplyCommentRule(item, "x");
        ItemAutoFailRules.ApplyCommentRule(e, "[framer] Saved comment");
        Check("A-004-24", new[] { a, b, c, d, e }.All(i => (string?)i.Value == "Fail") && (string?)f.Value == "Pass" && (string?)g.Value == "Yes",
            "A, B, C, E Fail; D still Fail; F still Pass; G still Yes");
        ItemAutoFailRules.ApplyCommentRule(a, "");
        Check("A-004-24", (string?)a.Value == "Fail", "clearing A's comment keeps Fail");

        string path = WriteFixture("a24", Fixture.Document(20, false, ("1", "Checks", "Simple", new[]
        {
            Fixture.Item(801, "1.1", "Note", "PassFailNaNi", "Pass", "Old note"),
            Fixture.Item(802, "1.2", "Blank with comment", "PassFailNaNi", null, "Gap", pictures: 1),
        })));
        byte[] before = File.ReadAllBytes(path);
        DateTime stamp = File.GetLastWriteTimeUtc(path);
        var saver = Saver(Path.GetDirectoryName(Path.GetDirectoryName(path))!);
        var model = saver.Load(path);
        saver.Save(model);
        Check("A-004-24", (string?)ModelItem(model, "1.1").Value == "Pass" && ModelItem(model, "1.2").Value == null && Unchanged(path, before, stamp),
            "comments and photos already in a file change nothing on open and close");

        var pa = I("PassFailNi", "Pass", "Gap at trim"); var pb = I("PassFailNi", "NI", "Gap at trim");
        var pc = I("PassFailNi", "Pass"); var pd = I("PassFailNi", null);
        foreach (var item in new[] { pa, pb, pc, pd }) ItemAutoFailRules.ApplyPhotoRule(item, item.Comments);
        Check("A-004-25", (string?)pa.Value == "Fail" && (string?)pb.Value == "NI" && (string?)pc.Value == "Pass" && pd.Value == null,
            "photo: A Fail, B NI, C Pass, D null");

        var sweep = new InspectionFile
        {
            Sections = new()
            {
                new Section
                {
                    Items = new()
                    {
                        I("PassFailNaNi", null, "Gap"), I("PassFailNaNi", "", null, 1), I("PassFailNaNi", "  ", "Gap"), I("PassFailNaNi", "Pass", "Gap"),
                        I("PassFailNaNi", "NI", null, 1), I("PassFailNaNi", null, "   "), I("YesNo", null, "Gap")
                    }
                }
            }
        };
        ItemAutoFailRules.ApplySaveCommandSweep(sweep);
        var s = sweep.Sections[0].Items;
        Check("A-004-26", s.Take(3).All(i => (string?)i.Value == "Fail") && (string?)s[3].Value == "Pass" && (string?)s[4].Value == "NI" &&
            s[5].Value == null && s[6].Value == null, "Save command: A, B, C Fail; D Pass, E NI; F and G null");

        var auto = new InspectionFile
        {
            Sections = new()
            {
                new Section
                {
                    Items = new()
                    {
                        I("PassFailNi", null, "[plumber] Leak at valve"), I("PassFailNi", null, "  [framer] Gap"), I("PassFailNi", null, "Leak at valve"),
                        I("PassFailNi", null, null, 1), I("PassFailNi", "Pass", "[plumber] Leak")
                    }
                }
            }
        };
        ItemAutoFailRules.ApplyAutoSaveSweep(auto);
        var t = auto.Sections[0].Items;
        Check("A-004-27", (string?)t[0].Value == "Fail" && (string?)t[1].Value == "Fail" && t[2].Value == null && t[3].Value == null && (string?)t[4].Value == "Pass",
            "automatic save: A, B Fail; C, D null; E Pass");
    }
}

static class Corpus
{
    private static readonly HashSet<string> Tokens = new(StringComparer.Ordinal)
        { "Pass", "Fail", "NI", "NA", "N/A", "Yes", "No", "Discussed", "ok", "[Uploaded]", "pass", "fail", "ni", "na", "yes", "no" };

    private static string Shape(string? value) =>
        value == null ? "(null)" : value.Length == 0 ? "(empty)"
            : new string(value.Select(ch => char.IsDigit(ch) ? '9' : char.IsLetter(ch) ? 'a' : ch).ToArray());

    private static string? StringValue(JToken? token) => token == null || token.Type == JTokenType.Null ? null : token.Type == JTokenType.String ? (string?)token : token.ToString(Formatting.None);

    public static int Run(string sandbox, string? reportPath)
    {
        var files = Directory.EnumerateFiles(sandbox, "*.ins", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        string work = Path.Combine(Path.GetTempPath(), "red-f004-corpus-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(work, "MyList"));
        var controls = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var checkboxValues = new SortedDictionary<string, SortedDictionary<string, int>>(StringComparer.Ordinal);
        var shapes = new SortedDictionary<string, SortedDictionary<string, int>>(StringComparer.Ordinal);
        var naRewrites = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var formFiles = new SortedDictionary<int, string>();
        int unchangedSaves = 0, failedOpens = 0, hiddenItems = 0, oldRuleHidden = 0, shownMismatch = 0, items = 0;
        int sortedLists = 0, unsortedLists = 0, inspectDialectSorted = 0, inspectDialectUnsorted = 0, documentUploads = 0;
        var problems = new List<string>();

        void Count(SortedDictionary<string, SortedDictionary<string, int>> table, string key, string value)
        {
            if (!table.TryGetValue(key, out var inner)) table[key] = inner = new SortedDictionary<string, int>(StringComparer.Ordinal);
            inner[value] = inner.TryGetValue(value, out int n) ? n + 1 : 1;
        }

        for (int index = 0; index < files.Count; index++)
        {
            string label = $"{Path.GetFileName(Path.GetDirectoryName(files[index]))}#{index}";
            JObject document;
            try { document = InspectionFileReader.ParseDocument(File.ReadAllBytes(files[index]), out _); }
            catch { failedOpens++; problems.Add($"{label}: does not parse"); continue; }
            int formId = (int?)document["FormId"] ?? 0;
            if (!formFiles.ContainsKey(formId)) formFiles[formId] = files[index];
            bool inspectDialect = Json.Items(document).All(i => i["ItemResultId"] == null);

            foreach (var item in Json.Items(document))
            {
                items++;
                string control = StringValue(item["ControlName"]) ?? "(null)";
                controls[control] = controls.TryGetValue(control, out int n) ? n + 1 : 1;
                string? value = StringValue(item["Value"]);
                var kind = ItemControls.Resolve(control).Kind;
                string group = StringValue(item["ItemGroupId"]) ?? "";
                if (group.Length > 0)
                    Count(shapes, $"group {StringValue(item["ItemGroupControlName"]) ?? "(null)"} / {control}", Shape(value));
                else if (control == "PassFail")
                    Count(shapes, "PassFail outside groups", Shape(value));
                if (kind == ItemControlKind.Checkboxes)
                    Count(checkboxValues, control, value == null ? "(null)" : value.Length == 0 ? "(empty)" : Tokens.Contains(value) ? value : "(other text)");
                if (kind is ItemControlKind.NumberPad or ItemControlKind.Time or ItemControlKind.Date or ItemControlKind.HomeReadiness)
                    Count(shapes, control, Shape(value));
                string name = StringValue(item["Name"]) ?? "";
                if (ItemControls.PhoneMaskItemNames.Contains(name)) Count(shapes, "phone:" + control, Shape(value));
                if (kind == ItemControlKind.DocumentUpload && !string.IsNullOrEmpty(value)) documentUploads++;
                if (kind == ItemControlKind.SearchLookup && item["ValueList"] is JArray { Count: > 1 } list)
                {
                    var values = list.Select(v => (string?)v ?? "").ToList();
                    bool sorted = values.SequenceEqual(values.OrderBy(v => v, StringComparer.CurrentCultureIgnoreCase));
                    if (sorted) sortedLists++; else unsortedLists++;
                    if (inspectDialect) { if (sorted) inspectDialectSorted++; else inspectDialectUnsorted++; }
                }
                bool hide = item["Hide"]?.Type == JTokenType.Boolean && (bool)item["Hide"]!;
                if (hide) hiddenItems++;
                bool oldHidden = item["HidePicturesButton"]?.Type == JTokenType.Boolean && (bool)item["HidePicturesButton"]! &&
                    item["HideCommentsButton"]?.Type == JTokenType.Boolean && (bool)item["HideCommentsButton"]! &&
                    !(item["Pictures"] as JArray ?? new JArray()).OfType<JObject>().Any(p => !string.IsNullOrWhiteSpace((string?)p["Image"]));
                if (oldHidden) oldRuleHidden++;
            }

            // V2 visibility and V3 round trip on a copy, through the real loader and Mode A save.
            string copy = Path.Combine(work, "MyList", "copy.ins");
            File.Copy(files[index], copy, true);
            var stamp = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(copy, stamp);
            byte[] before = File.ReadAllBytes(copy);
            try
            {
                var saver = new SurgicalSaveService(new FailedSaveRecoveryService(Path.Combine(work, "Recovery")), null, Path.Combine(work, "VerifiedSaves"));
                var model = saver.Load(copy);
                foreach (var item in model.Sections.SelectMany(s => s.Items).Where(i => i != null))
                {
                    bool hidden = ItemControls.IsHidden(item);
                    if (ChecklistItemVisibility.ShouldShow(item) == hidden) shownMismatch++;
                }
                var raw = Json.Items(document).ToList();
                var modelItems = model.Sections.SelectMany(s => s.Items).ToList();
                for (int i = 0; i < raw.Count && i < modelItems.Count; i++)
                    if (StringValue(raw[i]["Value"]) == "NA" && (string?)modelItems[i].Value == "NI")
                    {
                        string control = modelItems[i].ControlName ?? "(null)";
                        naRewrites[control] = naRewrites.TryGetValue(control, out int r) ? r + 1 : 1;
                    }
                saver.Save(model);
                if (File.ReadAllBytes(copy).SequenceEqual(before) && File.GetLastWriteTimeUtc(copy) == stamp) unchangedSaves++;
                else problems.Add($"{label}: an unedited open/close changed the file");
            }
            catch (Exception ex) { problems.Add($"{label}: {ex.GetType().Name}"); }
        }

        // SPEC §10 V1 / A-004-20: one file per FormId, every item set through the value paths RED's editors call.
        int formsRun = 0, valuesChecked = 0;
        var violations = new List<string>();
        foreach (var (formId, file) in formFiles)
        {
            string copy = Path.Combine(work, "MyList", $"form-{formId}.ins");
            File.Copy(file, copy, true);
            try
            {
                var saver = new SurgicalSaveService(new FailedSaveRecoveryService(Path.Combine(work, "Recovery")), null, Path.Combine(work, "VerifiedSaves"));
                var model = saver.Load(copy);
                var touched = new List<(Item Item, ItemControl Control)>();
                foreach (var item in model.Sections.SelectMany(s => s.Items).Where(i => i != null && ChecklistItemVisibility.ShouldShow(i)))
                {
                    var control = ItemControls.For(item);
                    switch (control.Kind)
                    {
                        case ItemControlKind.Checkboxes:
                            foreach (var choice in control.Choices!) item.Value = ItemControls.NextChoiceValue(item, choice);
                            break;
                        case ItemControlKind.NumberPad:
                        {
                            string? v = item.Value?.ToString();
                            foreach (var key in new[] { "CLEAR", "4", "¾" }) ItemControls.PressNumberPadKey(v, key, out v);
                            item.Value = control.HasNiToggle ? ItemControls.NextNiToggleValue(new Item { Value = v }) ?? v : v;
                            if (control.HasNiToggle) item.Value = "NI";
                            break;
                        }
                        case ItemControlKind.Text:
                        case ItemControlKind.Lookup:
                            item.Value = control.HasNiToggle ? "NI" : "Example";
                            break;
                        case ItemControlKind.HomeReadiness: item.Value = ItemControls.HomeReadinessChoices[6]; break;
                        case ItemControlKind.SearchLookup: ItemControls.AddSearchLookupValue(item, "Example Name"); break;
                        case ItemControlKind.Time: item.Value = ItemControls.FormatTime(new DateTime(2026, 3, 3, 17, 5, 0)); break;
                        case ItemControlKind.Date: item.Value = ItemControls.FormatDate(new DateTime(2026, 3, 3)); break;
                        case ItemControlKind.Memo: item.Comments = "Example narrative"; break;
                        case ItemControlKind.PictureUpload: item.Pictures.Add(new Picture { Data = Fixture.FakeJpeg(1), Title = "Picture" }); break;
                        default: continue; // DocumentUpload, DocumentButton, Button, ink and unknown names are not set here
                    }
                    touched.Add((item, control));
                }
                saver.Save(model);
                var saved = Json.ParseFile(copy);
                var savedItems = Json.Items(saved).ToList();
                var all = model.Sections.SelectMany(s => s.Items).ToList();
                foreach (var (item, control) in touched)
                {
                    int i = all.IndexOf(item);
                    string? stored = StringValue(savedItems[i]["Value"]);
                    valuesChecked++;
                    bool ok = control.Kind switch
                    {
                        ItemControlKind.Checkboxes => stored == null || control.Choices!.Contains(stored),
                        ItemControlKind.NumberPad => stored == "NI" && control.HasNiToggle || stored != null && ItemControls.TryParseNumber(stored, out double num) &&
                            ItemControls.FormatNumber(num) == stored && num <= ItemControls.NumberPadMaximum,
                        ItemControlKind.Time => ItemControls.TryParseTime(stored, out var tm) && ItemControls.FormatTime(tm) == stored,
                        ItemControlKind.Date => ItemControls.TryParseDate(stored, out var dt) && ItemControls.FormatDate(dt) == stored,
                        ItemControlKind.Memo => stored == "ok",
                        ItemControlKind.PictureUpload => stored == "[Uploaded]",
                        ItemControlKind.HomeReadiness => ItemControls.HomeReadinessChoices.Contains(stored),
                        _ => stored != null && stored.Length <= (control.MaxLength == 0 ? int.MaxValue : control.MaxLength)
                    };
                    if (!ok) violations.Add($"FormId {formId}: {control.Name} stored {Shape(stored)}");
                }
                formsRun++;
            }
            catch (Exception ex) { violations.Add($"FormId {formId}: {ex.GetType().Name}"); }
        }
        try { Directory.Delete(work, true); } catch { }

        var report = new StringBuilder();
        report.AppendLine("# F-004 corpus run (sandbox copy)");
        report.AppendLine();
        report.AppendLine($"Files: {files.Count}, parse failures: {failedOpens}, items: {items}.");
        report.AppendLine($"V3 unedited open/close wrote nothing: {unchangedSaves}/{files.Count - failedOpens}.");
        report.AppendLine($"V2 rows shown = non-hidden rows: {(shownMismatch == 0 ? "yes" : $"NO ({shownMismatch} mismatches)")}; Hide true: {hiddenItems}; rows the old Hide*Button rule hid: {oldRuleHidden}.");
        report.AppendLine($"A-004-20: {formsRun} FormIds, {valuesChecked} values set through the editor paths, {violations.Count} outside their §3 set.");
        foreach (var v in violations.Distinct().Take(40)) report.AppendLine("- " + v);
        report.AppendLine();
        report.AppendLine("## ControlName census (exact names)");
        foreach (var (name, count) in controls)
            report.AppendLine($"- {name}: {count}{(ItemControls.Resolve(name).Kind == ItemControlKind.Unsupported ? "  (Unsupported in RED/INSPECT)" : "")}");
        report.AppendLine();
        report.AppendLine("## Checkbox values per ControlName");
        foreach (var (name, values) in checkboxValues)
            report.AppendLine($"- {name}: " + string.Join(", ", values.Select(kv => $"{kv.Key} {kv.Value}")));
        report.AppendLine();
        report.AppendLine("## V1 NA→NI rewrites on open (in memory)");
        report.AppendLine(naRewrites.Count == 0 ? "- none" : string.Join("\n", naRewrites.Select(kv => $"- {kv.Key}: {kv.Value}")));
        report.AppendLine();
        report.AppendLine("## Value shapes (digits as 9, letters as a)");
        foreach (var (name, values) in shapes)
            report.AppendLine($"- {name}: " + string.Join(", ", values.OrderByDescending(kv => kv.Value).Take(12).Select(kv => $"`{kv.Key}` {kv.Value}")));
        report.AppendLine();
        report.AppendLine($"## SearchLookup ValueList order: sorted {sortedLists}, unsorted {unsortedLists}; INSPECT-dialect files: sorted {inspectDialectSorted}, unsorted {inspectDialectUnsorted}");
        report.AppendLine($"DocumentUpload items with a value: {documentUploads}");
        if (problems.Count > 0)
        {
            report.AppendLine();
            report.AppendLine("## Problems");
            foreach (var p in problems.Take(50)) report.AppendLine("- " + p);
        }
        Console.WriteLine(report.ToString());
        if (reportPath != null) File.WriteAllText(reportPath, report.ToString());
        return problems.Count == 0 && violations.Count == 0 && shownMismatch == 0 ? 0 : 1;
    }
}
