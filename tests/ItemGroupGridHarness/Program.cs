// F-005 item-group grid harness (program/features/F-005-item-group-grids/ACCEPTANCE.md).
//   dotnet run      fixture checks in a fresh temp sandbox
// Fixtures are synthetic (job 1234567, initials XX, 100 Example St). No real inspection is read.
using System.Text;
using InspectionEditor.Models;
using InspectionEditor.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

if (args.Length >= 2 && args[0] == "--ui-fixture")
{
    // One synthetic CPR report for clicking through the F-005..F-016 panes in an isolated dev build.
    Fx.Write(args[1], Fx.UiDocument());
    Console.WriteLine("wrote UI fixture");
    return 0;
}
return Harness.Run();

static class Fx
{
    public static readonly UTF8Encoding Utf8 = new(false);
    private static int _id = 1000;

    public static JObject Item(string number, string name, string control, string? value, string groupId = "",
        string? groupControl = null, bool required = false) => new()
    {
        ["ItemId"] = ++_id, ["Name"] = name, ["Number"] = number, ["ControlName"] = control,
        ["ControlFormatString"] = null, ["ItemGroupId"] = groupId, ["ItemGroupControlName"] = groupControl,
        ["PictureSizeId"] = 0, ["IsCopied"] = false, ["Hide"] = false, ["DisplayLabel"] = number + " " + name,
        ["MaxIncidents"] = 0, ["CommentList"] = new JArray(), ["ValueList"] = new JArray(), ["SortOrder"] = 0,
        ["Value"] = value, ["ResultSortOrder"] = 0, ["Comments"] = "", ["Incidents"] = null, ["Pictures"] = new JArray(),
        ["Required"] = required, ["AlwaysPrint"] = false, ["OnlyPrint"] = "", ["Template"] = null, ["ButtonText"] = null,
        ["HideCommentsButton"] = true, ["HidePicturesButton"] = true, ["HideAddButton"] = true, ["IsPictureRequired"] = false
    };

    public static JObject Section(int id, string number, string name, params JObject[] items) => new()
    {
        ["SectionId"] = id, ["Name"] = name, ["ControlName"] = "SectionStandardControl", ["Number"] = number,
        ["Items"] = new JArray(items)
    };

    public static JObject Document(int formId, string code, params JObject[] sections) => new()
    {
        ["InspectVersion"] = null, ["WorkOrderId"] = "00000000-0000-0000-0000-000000000001", ["ServiceId"] = 61,
        ["FormId"] = formId, ["InspectionName"] = "Example Inspection", ["InspectionNumber"] = $"1234567-{code}-1-XX",
        ["InspectionCode"] = code, ["Attempt"] = 1, ["StatusId"] = 2, ["JobNumber"] = "1234567",
        ["Address"] = "100 Example St", ["City"] = "Example City", ["State"] = "TX",
        ["InspectionFee_Secure"] = "PLACEHOLDERCIPHERTEXTA0==", ["Sections"] = new JArray(sections),
        ["Events"] = new JArray(), ["Attachments"] = new JArray()
    };

    // FormId 27 section 4: "Truck {n} Arrival|Gallons|Slump|Temp", numbered 4.{n}.A/G/S/T (RTC B51).
    public static JObject[] Trucks(Func<int, string, string?>? value = null, string groupId = "1")
    {
        var items = new List<JObject>();
        for (int n = 1; n <= 15; n++)
            foreach (var type in ItemGroupLayout.TruckTypes)
                items.Add(Item($"4.{n}.{type[0]}", $"Truck {n} {type}", "PassFail", value?.Invoke(n, type) ?? "", groupId, "TruckPlacement"));
        return items.ToArray();
    }

    public static JObject[] Slab(string group, string groupId, string prefix, int start, string? value = "")
    {
        var items = new List<JObject>();
        for (int i = 0; i < 12; i++)
            items.Add(Item($"8.{start + i}", $"{prefix} {i + 1}", "NumberPad", value, groupId, group));
        return items.ToArray();
    }

    public static JObject[] Drainage(int rows = 14)
    {
        var items = new List<JObject>();
        for (int n = 1; n <= rows; n++)
            foreach (var type in ItemGroupLayout.DrainageTypes)
                items.Add(Item($"2.{n}.{type[0]}", $"{type} {n}", "NumberPad", "", "1", "DrainageVerification"));
        return items.ToArray();
    }

    public static JObject UiDocument()
    {
        var cylinders = Item("1.3", "Test cylinders included", "CPRYesNo", null);
        cylinders["ItemId"] = 1615;
        var super = Item("1.1", "Superintendent", "Text", "Example Super");
        var memo = Item("1.2", "Comment 1", "Memo", null);
        var sketch = Item("5.1", "Sketch", "Sketch", null);
        var foundation = Item("5.2", "Foundation Sketch", "FoundationSketch", null);
        foreach (var i in new[] { super, memo, cylinders, sketch, foundation })
        {
            i["HideCommentsButton"] = false; i["HidePicturesButton"] = false; i["HideAddButton"] = false;
        }
        var doc = Document(27, "CPR",
            Section(1, "1", "General", super, memo, cylinders),
            Section(4, "4", "Truck Placement", Trucks()),
            Section(2, "2", "Drainage", Drainage(4)),
            Section(5, "5", "Sketches", sketch, foundation));
        doc["InspectionNumber"] = "1234567-CPR-1-XX";
        doc["StatusId"] = 1;
        doc["StatusList"] = new JArray(new JObject { ["Id"] = 0, ["Name"] = "Ordered" }, new JObject { ["Id"] = 1, ["Name"] = "Scheduled" },
            new JObject { ["Id"] = 2, ["Name"] = "Passed" }, new JObject { ["Id"] = 3, ["Name"] = "Failed" });
        foreach (var (k, v) in new (string, JToken)[] {
            ("NextActionId", 0), ("NextActionText", " "), ("PlanNumber", "P1"), ("BuilderName", "Example Builder"), ("Contact", "Example Contact"),
            ("ContactNumber", "000-000-0000"), ("Project", "Example Project"), ("Lot", "1"), ("Block", "A"), ("InspectorName", "Pat Q Example"),
            ("DateInspected", "2026-10-18T09:00:00-05:00"), ("StructureTypeId", 0), ("IsReinspection", false), ("PurchaseOrderRequired", false),
            ("PurchaseOrderNumber", ""), ("RequisitionNumber", JValue.CreateNull()), ("Requests", JValue.CreateNull()), ("WhoNotified", JValue.CreateNull()),
            ("WhenNotified", JValue.CreateNull()), ("Signature", JValue.CreateNull()), ("InternalNotes", JValue.CreateNull()),
            ("InspectorOtherFee1_Secure", JValue.CreateNull()), ("InspectorOtherFee2_Secure", JValue.CreateNull()),
            ("OtherFeeDescription1", JValue.CreateNull()), ("OtherFeeDescription2", JValue.CreateNull()) })
        {
            var sections = doc.Property("Sections")!;
            sections.AddBeforeSelf(new JProperty(k, v));
        }
        return doc;
    }

    public static Section ModelSection(JObject document, int index) =>
        document.ToObject<InspectionFile>()!.Sections[index];

    public static string Write(string folder, JObject document)
    {
        string path = Path.Combine(folder, (string)document["InspectionNumber"]! + ".ins");
        File.WriteAllBytes(path, Utf8.GetBytes(document.ToString(Formatting.None)));
        return path;
    }

    public static JObject Read(string path) => JObject.Parse(Utf8.GetString(File.ReadAllBytes(path)));

    public static IEnumerable<JObject> Items(JObject doc, int section) =>
        ((JArray)doc["Sections"]![section]!["Items"]!).OfType<JObject>();
}

static class Harness
{
    private static int _passed, _failed;

    private static void Check(string id, bool ok, string what)
    {
        if (ok) { _passed++; Console.WriteLine($"PASS {id} {what}"); }
        else { _failed++; Console.WriteLine($"FAIL {id} {what}"); }
    }

    public static int Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "red-f005-" + Guid.NewGuid().ToString("N"));
        string myList = Path.Combine(root, "Inspections", "MyList");
        Directory.CreateDirectory(myList);
        SurgicalSaveService NewSaver() => new(new FailedSaveRecoveryService(Path.Combine(root, "Recovery")), null,
            Path.Combine(root, "VerifiedSaves"));
        try
        {
            // ---------- A-005-01 CPR truck grid renders (layout) ----------
            {
                var doc = Fx.Document(27, "CPR",
                    Fx.Section(2, "2", "Pour", Fx.Item("2.1", "Pump truck", "PassFailNaNi", "Pass")),
                    Fx.Section(4, "4", "Truck Placement", Fx.Trucks((n, t) => n == 2 && t == "Gallons" ? "9" : "")));
                var section = Fx.ModelSection(doc, 1);
                var runs = ItemGroupLayout.Detect(section);
                var run = runs.SingleOrDefault();
                Check("A-005-01", run is { Recognized: true, Kind: ItemGroupKind.TruckPlacement, RowCount: 15, ColumnCount: 4 } &&
                                  run.ColumnHeaders.SequenceEqual(new[] { "Arrival time", "Gallons", "Slump", "Temp (F)" }),
                    "section 4 is one 15x4 grid (Arrival time, Gallons, Slump, Temp)");
                Check("A-005-01", run != null && section.Items.All(i => run.CellFor(i) != null) &&
                                  run.CellAt(1, 1)!.Item.Name == "Truck 2 Gallons" && run.CellAt(1, 1)!.Item.Value?.ToString() == "9" &&
                                  run.CellAt(0, 0)!.Kind == ItemGroupCellKind.Time && run.CellAt(14, 3)!.Kind == ItemGroupCellKind.Number,
                    "all 60 items are cells (none left as rows); cells bind by name");

                // Binding is by Name, not by position: shuffle the run.
                var shuffled = Fx.Trucks();
                Array.Reverse(shuffled);
                var shuffledRun = ItemGroupLayout.Detect(Fx.ModelSection(Fx.Document(27, "CPR", Fx.Section(4, "4", "Trucks", shuffled)), 0)).Single();
                Check("G3", shuffledRun.Recognized && shuffledRun.CellAt(0, 0)!.Item.Name == "Truck 1 Arrival" &&
                            shuffledRun.CellAt(14, 3)!.Item.Name == "Truck 15 Temp",
                    "TruckPlacement binds by Name even when the run order differs");
            }

            // ---------- A-005-02 Truck time format ----------
            {
                bool ok = ItemGroupLayout.TryParseTruckTime("8:25", out string stored);
                Check("A-005-02", ok && stored == "08:25 AM" && ItemGroupLayout.FormatTruckTimeDisplay(stored) == "08:25",
                    "8:25 in the morning stores \"08:25 AM\" and displays 08:25");
                var cases = new (string In, string Stored, string Shown)[]
                {
                    ("08:25", "08:25 AM", "08:25"), ("20:25", "08:25 PM", "20:25"), ("8:25 pm", "08:25 PM", "20:25"),
                    ("825", "08:25 AM", "08:25"), ("0005", "12:05 AM", "00:05"), ("12:00 PM", "12:00 PM", "12:00"),
                    ("12:00 am", "12:00 AM", "00:00"), ("7a", "07:00 AM", "07:00"), ("", "", "")
                };
                foreach (var c in cases)
                {
                    bool parsed = ItemGroupLayout.TryParseTruckTime(c.In, out string s);
                    Check("C2", parsed && s == c.Stored && ItemGroupLayout.FormatTruckTimeDisplay(s) == c.Shown,
                        $"time \"{c.In}\" stores \"{c.Stored}\" and shows \"{c.Shown}\"");
                }
                foreach (var bad in new[] { "25:00", "8:61", "13:00 PM", "abc", "8:5", "0:30 AM" })
                    Check("C2", !ItemGroupLayout.TryParseTruckTime(bad, out _), $"time \"{bad}\" is refused");
                Check("C2", ItemGroupLayout.FormatTruckTimeDisplay("odd value") == "odd value",
                    "a stored time RED cannot read is shown as stored (never rewritten on open)");
            }

            // ---------- A-005-03 Untouched grid unchanged; V2 no-edit save; V3 only edited Values change ----------
            {
                var doc = Fx.Document(27, "CPR",
                    Fx.Section(2, "2", "Pour", Fx.Item("2.1", "Pump truck", "PassFailNaNi", "Pass")),
                    Fx.Section(4, "4", "Truck Placement", Fx.Trucks((n, t) => n == 1 && t == "Arrival" ? "07:10 AM" : n == 3 ? null : "")),
                    Fx.Section(8, "8", "Measurements",
                        Fx.Slab("SlabExtMeasures", "1", "Exterior", 1, "6")
                            .Concat(Fx.Slab("SlabIntMeasures", "2", "Interior", 13, "")).ToArray()));
                string path = Fx.Write(myList, doc);
                byte[] original = File.ReadAllBytes(path);
                var before = Fx.Read(path);

                var saver = NewSaver();
                var model = saver.Load(path);
                saver.Save(model);
                Check("V2", File.ReadAllBytes(path).SequenceEqual(original), "open + save with no edits writes nothing");

                model = saver.Load(path);
                model.Sections[0].Items[0].Value = "Fail"; // edit only section 2
                saver.Save(model);
                var after = Fx.Read(path);
                bool section4Same = Fx.Items(before, 1).Zip(Fx.Items(after, 1))
                    .All(p => p.First.ToString(Formatting.None) == p.Second.ToString(Formatting.None));
                Check("A-005-03", section4Same && Fx.Items(after, 1).Count() == 60,
                    "editing only section 2 leaves every section 4 item byte-identical (\"\" and null cells kept)");

                // V3: edit one truck time and one slab value through the grid rules.
                model = saver.Load(path);
                var truckRun = ItemGroupLayout.Detect(model.Sections[1]).Single();
                var slabRun = ItemGroupLayout.Detect(model.Sections[2]).First();
                var timeCell = truckRun.CellAt(4, 0)!;  // Truck 5 Arrival
                var slabCell = slabRun.CellAt(2, 1)!;   // Corner 3, TOF to BOB
                ItemGroupLayout.TryParseTruckTime("13:05", out string t);
                ItemGroupLayout.TryNormalizeNumber("7.50", out string n, out _);
                EditorEditService.SetValue(model, timeCell.Item, t);
                EditorEditService.SetValue(model, slabCell.Item, n);
                saver.Save(model);
                var edited = Fx.Read(path);
                var changed = new List<string>();
                for (int s = 0; s < 3; s++)
                    foreach (var (a, b) in Fx.Items(after, s).Zip(Fx.Items(edited, s)))
                    {
                        foreach (var p in a.Properties())
                            if (b[p.Name]?.ToString(Formatting.None) != p.Value.ToString(Formatting.None))
                                changed.Add($"{a["Number"]}.{p.Name}");
                    }
                Check("V3", changed.OrderBy(x => x).SequenceEqual(new[] { "4.5.A.Value", "8.8.Value" }) &&
                            (string?)Fx.Items(edited, 1).First(i => (string?)i["Number"] == "4.5.A")["Value"] == "01:05 PM" &&
                            (string?)Fx.Items(edited, 2).First(i => (string?)i["Number"] == "8.8")["Value"] == "7.5",
                    $"only the two edited items' Value changed; time written as hh:mm tt (changed: {string.Join(", ", changed)})");
            }

            // ---------- A-005-04 No copy inside grids (rule; the buttons are hidden by IsGroupedItem) ----------
            {
                var section = Fx.ModelSection(Fx.Document(27, "CPR", Fx.Section(4, "4", "Trucks", Fx.Trucks())), 0);
                Check("A-005-04", section.Items.All(ItemGroupLayout.IsGroupedItem),
                    "every truck cell is a grouped item, so Copy and Remove are not offered and section Pass/NI and auto-Fail skip it");
                var plain = Fx.ModelSection(Fx.Document(27, "CPR", Fx.Section(2, "2", "Pour", Fx.Item("2.1", "Pump", "PassFail", ""))), 0);
                Check("A-005-04", !ItemGroupLayout.IsGroupedItem(plain.Items[0]), "an ordinary PassFail item is not grouped");
                var unknown = Fx.ModelSection(Fx.Document(27, "CPR", Fx.Section(2, "2", "X", Fx.Item("2.1", "A", "PassFail", "", "1", "MysteryGrid"))), 0);
                Check("G1", !ItemGroupLayout.IsGroupedItem(unknown.Items[0]), "an unknown group name keeps the item's own ControlName rules");
            }

            // ---------- A-005-05 Slab grids ----------
            {
                var doc = Fx.Document(20, "CPP", Fx.Section(8, "8", "Measurements",
                    Fx.Slab("SlabExtMeasures", "1", "Exterior", 1).Concat(Fx.Slab("SlabIntMeasures", "2", "Interior", 13)).ToArray()));
                var runs = ItemGroupLayout.Detect(Fx.ModelSection(doc, 0));
                Check("A-005-05", runs.Count == 2 &&
                                  runs[0] is { Recognized: true, Kind: ItemGroupKind.SlabExtMeasures, RowCount: 4, ColumnCount: 3 } &&
                                  runs[0].ColumnHeaders.SequenceEqual(new[] { "TOF to TOG", "TOF to BOB", "BW" }) &&
                                  runs[1] is { Recognized: true, Kind: ItemGroupKind.SlabIntMeasures, RowCount: 4, ColumnCount: 3 } &&
                                  runs[1].ColumnHeaders.SequenceEqual(new[] { "SD", "BW", "BD" }),
                    "section 8 shows a 4x3 exterior grid and a 4x3 interior grid");
                Check("G3", runs[0].CellAt(0, 0)!.Item.Number == "8.1" && runs[0].CellAt(1, 0)!.Item.Number == "8.4" &&
                            runs[0].CellAt(3, 2)!.Item.Number == "8.12" && runs[1].CellAt(0, 0)!.Item.Number == "8.13",
                    "slab cells bind by position 0-11, three per corner/location");

                var eleven = Fx.Slab("SlabExtMeasures", "1", "Exterior", 1).Take(11).ToArray();
                var shortRun = ItemGroupLayout.Detect(Fx.ModelSection(Fx.Document(20, "CPP", Fx.Section(8, "8", "M", eleven)), 0)).Single();
                Check("G3", !shortRun.Recognized && shortRun.Problem != null, "a slab run that is not exactly 12 items falls back to rows");
            }

            // ---------- A-005-06 Drainage result calculation ----------
            {
                var doc = Fx.Document(133, "DVS", Fx.Section(2, "2", "Drainage", Fx.Drainage()));
                var section = Fx.ModelSection(doc, 0);
                var run = ItemGroupLayout.Detect(section).Single();
                Check("A-005-06", run is { Recognized: true, RowCount: 14, ColumnCount: 5 } &&
                                  run.CellAt(0, 4)!.Kind == ItemGroupCellKind.Result, "FormId 133 is a 14x5 grid with a Result column");
                var expected = run.CellAt(0, 1)!.Item;
                var shot = run.CellAt(0, 3)!.Item;
                var result = run.CellAt(0, 4)!.Item;
                expected.Value = "100.00";
                Check("A-005-06", ItemGroupLayout.UpdateDrainageResult(run, expected) == null && (result.Value?.ToString() ?? "") == "",
                    "with Shot Elevation blank the Result stays \"\"");
                shot.Value = "100.04";
                var changedItem = ItemGroupLayout.UpdateDrainageResult(run, shot);
                Check("A-005-06", ReferenceEquals(changedItem, result) && result.Value?.ToString() == "Pass",
                    "Expected 100.00 and Shot 100.04 store \"Pass\"");
                shot.Value = "100.06";
                ItemGroupLayout.UpdateDrainageResult(run, shot);
                Check("A-005-06", result.Value?.ToString() == "Fail", "Shot 100.06 stores \"Fail\"");
                shot.Value = "99.95";
                ItemGroupLayout.UpdateDrainageResult(run, shot);
                Check("SPEC 3.4", result.Value?.ToString() == "Pass", "|E - S| = 0.05 exactly is Pass");
                shot.Value = "x";
                ItemGroupLayout.UpdateDrainageResult(run, shot);
                Check("SPEC 3.4", result.Value?.ToString() == "", "a Shot Elevation that is not a decimal gives \"\"");
                var other = run.CellAt(5, 0)!.Item;
                Check("SPEC 3.4", ItemGroupLayout.UpdateDrainageResult(run, other) == null,
                    "editing Calc Point Id does not touch any Result");
            }

            // ---------- A-005-08 Unrecognised layout falls back ----------
            {
                var seven = Enumerable.Range(1, 7).Select(i => Fx.Item($"4.{i}", $"Something else {i}", "PassFail", "", "1", "TruckPlacement")).ToArray();
                var run = ItemGroupLayout.Detect(Fx.ModelSection(Fx.Document(27, "CPR", Fx.Section(4, "4", "Trucks", seven)), 0)).Single();
                Check("A-005-08", !run.Recognized && run.Problem != null && run.Items.Count == 7 &&
                                  ItemGroupLayout.NotRecognisedText == "Grid layout not recognised",
                    "7 unrelated TruckPlacement items: \"Grid layout not recognised\", items render as rows");

                var dup = Fx.Trucks();
                dup[5]["Name"] = "Truck 1 Arrival";
                var dupRun = ItemGroupLayout.Detect(Fx.ModelSection(Fx.Document(27, "CPR", Fx.Section(4, "4", "Trucks", dup)), 0)).Single();
                Check("G3", !dupRun.Recognized && dupRun.Problem != null, "a duplicated truck name falls back to rows");

                var missing = Fx.Trucks().Take(59).ToArray();
                var missingRun = ItemGroupLayout.Detect(Fx.ModelSection(Fx.Document(27, "CPR", Fx.Section(4, "4", "Trucks", missing)), 0)).Single();
                Check("G3", !missingRun.Recognized && missingRun.Problem != null, "a missing truck name falls back to rows");

                var drainageShort = Fx.Drainage(13);
                var drainageRun = ItemGroupLayout.Detect(Fx.ModelSection(Fx.Document(133, "DVS", Fx.Section(2, "2", "D", drainageShort)), 0)).Single();
                Check("G3", !drainageRun.Recognized && drainageRun.Problem != null, "a drainage run with a missing row falls back to rows");

                var unknown = Fx.Item("2.1", "A", "PassFail", "", "1", "MysteryGrid");
                var unknownRun = ItemGroupLayout.Detect(Fx.ModelSection(Fx.Document(1, "X", Fx.Section(2, "2", "X", unknown)), 0)).Single();
                Check("G1", !unknownRun.Recognized && unknownRun.Problem == null,
                    "an unknown group name renders normal rows without the grid notice (INSPECT renders nothing)");
            }

            // ---------- G1 grouping: consecutive runs only ----------
            {
                var items = new[]
                {
                    Fx.Item("8.1", "A", "NumberPad", "", "1", "SlabExtMeasures"),
                    Fx.Item("8.2", "B", "NumberPad", "", "1", "SlabExtMeasures"),
                    Fx.Item("8.3", "Gap", "PassFail", ""),
                    Fx.Item("8.4", "C", "NumberPad", "", "1", "SlabExtMeasures")
                };
                var runs = ItemGroupLayout.Detect(Fx.ModelSection(Fx.Document(20, "CPP", Fx.Section(8, "8", "M", items)), 0));
                Check("G1", runs.Count == 2 && runs[0].Items.Count == 2 && runs[1].Items.Count == 1,
                    "the same ItemGroupId split by another item makes two runs");
                var nullIds = Fx.Item("2.1", "A", "PassFail", "");
                nullIds["ItemGroupId"] = null;
                Check("G1", ItemGroupLayout.Detect(Fx.ModelSection(Fx.Document(1, "X", Fx.Section(2, "2", "X", nullIds)), 0)).Count == 0,
                    "a null or empty ItemGroupId is not a group");
            }

            // ---------- C2 number cells (F-004 N5/N7) ----------
            {
                var numberCases = new (string In, string Out)[] { ("3.50", "3.5"), ("12", "12"), (" 7.25 ", "7.25"), ("0.5", "0.5"), ("99999", "99999"), ("", "") };
                foreach (var c in numberCases)
                    Check("C2", ItemGroupLayout.TryNormalizeNumber(c.In, out string s, out _) && s == c.Out,
                        $"number \"{c.In}\" stores \"{c.Out}\"");
                Check("C2", !ItemGroupLayout.TryNormalizeNumber("100000", out _, out string? tooLarge) &&
                            tooLarge == "Value cannot be greater than 99999.00", "above 99999 is refused with INSPECT's message");
                Check("C2", !ItemGroupLayout.TryNormalizeNumber("1,000", out _, out _) && !ItemGroupLayout.TryNormalizeNumber("abc", out _, out _),
                    "thousands separators and text are refused");
            }

            // ---------- Required cells ----------
            {
                var trucks = Fx.Trucks();
                trucks[0]["Required"] = true;
                var run = ItemGroupLayout.Detect(Fx.ModelSection(Fx.Document(27, "CPR", Fx.Section(4, "4", "Trucks", trucks)), 0)).Single();
                var cell = run.CellAt(0, 0)!;
                Check("SPEC 3.1", ItemGroupLayout.IsRequiredMissing(cell.Item) && ItemGroupLayout.RequiredMissingText == "Required value is missing.",
                    "a blank Required cell reports \"Required value is missing.\"");
                cell.Item.Value = "08:00 AM";
                Check("SPEC 3.1", !ItemGroupLayout.IsRequiredMissing(cell.Item), "a filled Required cell is not missing");
            }
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine($"F-005 harness: {_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }
}
