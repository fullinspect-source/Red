// F-002 INS round-trip fidelity harness (program/features/F-002-ins-roundtrip/ACCEPTANCE.md).
//   dotnet run                         fixture checks A-002-01..17 in a fresh temp sandbox
//   dotnet run -- --corpus <SBX> [report.md]
//                                      A-002-02, A-002-10, SPEC V4/V5 and Q-21 over a sandbox COPY of Inspections
// Fixtures are synthetic (job 1234567, initials XX, 100 Example St, placeholder fee ciphertexts).
// Reports name files by folder and index only: no names, addresses, fees or other report text.
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using InspectionEditor.Models;
using InspectionEditor.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

if (args.Length >= 2 && args[0] == "--corpus")
    return Corpus.Run(args[1], args.Length > 2 ? args[2] : null);
return Fixtures.Run();

static class Json
{
    public static readonly UTF8Encoding Utf8 = new(false);

    public static JObject Parse(byte[] bytes) => InspectionFileReader.ParseDocument(bytes, out _);
    public static JObject ParseFile(string path) => Parse(File.ReadAllBytes(path));
    public static string Compact(JToken token) => token.ToString(Formatting.None);

    public static void Write(string path, JObject document) => File.WriteAllBytes(path, Utf8.GetBytes(Compact(document)));

    /// <summary>JSON paths (keys and indices only, never values) where two documents differ.</summary>
    public static List<string> Diff(JToken a, JToken b, string path = "$")
    {
        var differences = new List<string>();
        if (a.Type != b.Type) { differences.Add(path + " (type)"); return differences; }
        switch (a)
        {
            case JObject oa:
                var ob = (JObject)b;
                foreach (var p in oa.Properties())
                {
                    if (!ob.ContainsKey(p.Name)) differences.Add(path + "." + p.Name + " (removed)");
                    else differences.AddRange(Diff(p.Value, ob[p.Name]!, path + "." + p.Name));
                }
                foreach (var p in ob.Properties())
                    if (!oa.ContainsKey(p.Name)) differences.Add(path + "." + p.Name + " (added)");
                if (differences.Count == 0 && !oa.Properties().Select(x => x.Name).SequenceEqual(ob.Properties().Select(x => x.Name)))
                    differences.Add(path + " (key order)");
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

    public static string Normalize(string path) => Regex.Replace(path, @"\[\d+\]", "[*]");

    public static IEnumerable<JObject> Items(JObject document) =>
        (document["Sections"] as JArray ?? new JArray()).OfType<JObject>()
            .SelectMany(s => (s["Items"] as JArray ?? new JArray()).OfType<JObject>());

    public static IEnumerable<JObject> Pictures(JObject document) =>
        Items(document).SelectMany(i => (i["Pictures"] as JArray ?? new JArray()).OfType<JObject>());

    public static readonly string[] FeeKeys =
        { "InspectionFee_Secure", "InspectorOtherFee1_Secure", "InspectorOtherFee2_Secure", "OtherFeeDescription1", "OtherFeeDescription2" };

    public static readonly string[] ServerOnlyTopLevel =
        { "ElectricProvider", "GasProvider", "WaterProvider", "Rejected", "ErrorCount", "Filename", "RedPlanCheck" };
}

static class Fixture
{
    private static int _guid;
    public static string Guid() => new System.Guid(++_guid, 0x0f02, 0x4002, new byte[] { 0x80, 0, 0, 0, 0, 0, 0, 1 }).ToString();
    public static string FakeJpeg(int seed) => Convert.ToBase64String(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, (byte)seed, 1, 2, 3, 4, 5, 6, 7 });

    public static JObject ServerPicture(int seed, string description) => new()
    {
        ["Path"] = null, ["Caption"] = "Picture", ["Description"] = description, ["Image"] = FakeJpeg(seed),
        ["ServerPath"] = @"\\server\share\photos\" + Guid() + ".jpg", ["PictureId"] = Guid()
    };

    public static JObject Shell() => new()
    {
        ["Path"] = null, ["Caption"] = "Picture", ["Description"] = null, ["Image"] = null, ["ServerPath"] = null, ["PictureId"] = null
    };

    public static JObject ServerItem(int itemId, string number, string name, string control, string? value,
        string? comments = "", bool required = false, int rso = 0, bool copied = false, JArray? pictures = null,
        bool itemResultId = true, int pictureSizeId = 0, JArray? valueList = null, bool pictureRequired = false, int sortOrder = 0)
    {
        var item = new JObject
        {
            ["ItemId"] = itemId, ["Name"] = name, ["Number"] = number, ["ControlName"] = control,
            ["ControlFormatString"] = null, ["ItemGroupId"] = "", ["ItemGroupControlName"] = null,
            ["PictureSizeId"] = pictureSizeId, ["IsCopied"] = copied, ["Hide"] = false,
            ["DisplayLabel"] = number + " " + name + (required ? "<size=9><color=255, 0, 0> *</color></size>" : ""),
            ["MaxIncidents"] = 0, ["CommentList"] = new JArray(), ["ValueList"] = valueList ?? new JArray(),
            ["SortOrder"] = sortOrder, ["Value"] = value, ["ResultSortOrder"] = rso, ["Comments"] = comments,
            ["Incidents"] = null, ["Pictures"] = pictures ?? new JArray(), ["Required"] = required,
            ["AlwaysPrint"] = false, ["OnlyPrint"] = "", ["Template"] = null, ["ButtonText"] = null,
            ["HideCommentsButton"] = false, ["HidePicturesButton"] = false, ["HideAddButton"] = false,
            ["IsPictureRequired"] = pictureRequired
        };
        if (itemResultId) item["ItemResultId"] = Guid();
        return item;
    }

    private static JObject Section(int id, string number, string name, string? control, params JObject[] items) => new()
    {
        ["SectionId"] = id, ["Name"] = name, ["ControlName"] = control, ["Number"] = number, ["Items"] = new JArray(items)
    };

    /// <summary>Server (InspectForm) dialect: 69 top-level keys in server order.</summary>
    public static JObject Server(string code)
    {
        string number = $"1234567-{code}-1-XX";
        var sections = new JArray
        {
            Section(1, "1", "Administrative", "SectionStandardControl",
                ServerItem(101, "1.1", "Plan Number", "Text", "Example")),
            Section(2, "2", "Footings", "SectionStandardControl",
                ServerItem(201, "2.1", "Forms", "PassFailNaNi", "Pass")),
            Section(3, "3", "Slab", null,
                ServerItem(304, "3.4", "Vapor barrier", "Lookup", "A", pictureSizeId: 2,
                    valueList: new JArray("A", "B"), pictureRequired: true),
                ServerItem(700, "3.9", "Shared item", "PassFailNaNi", "Pass")),
            Section(4, "4", "Steel", "SectionStandardControl",
                ServerItem(700, "4.1", "Shared item", "PassFailNaNi", "Pass", rso: 1),
                ServerItem(402, "4.2", "Rebar", "PassFailNaNi", "Pass"),
                ServerItem(403, "4.3", "Chairs", "PassFailNaNi", "Pass", comments: null),
                ServerItem(405, "4.5", "Cables", "PassFailNaNi", "Pass", comments: "Old note",
                    pictures: new JArray(ServerPicture(1, "first"), ServerPicture(2, "second"))),
                ServerItem(406, "4.6", "Beams", "PassFailNaNi", "Pass",
                    pictures: new JArray(Shell(), ServerPicture(3, "kept"))),
                ServerItem(412, "4.12", "Anchor bolts", "PassFailNaNi", "Pass", required: true)),
            Section(5, "5", "Final", "SectionStandardControl",
                ServerItem(501, "5.1", "Cleanup", "PassFailNaNi", "Pass", comments: "original"),
                ServerItem(501, "5.1", "Cleanup", "PassFailNaNi", "Fail", comments: "copy A text", rso: 1, copied: true),
                ServerItem(501, "5.1", "Cleanup", "PassFailNaNi", "Fail", comments: "copy B text", rso: 2, copied: true))
        };
        var events = new JArray(
            new JObject { ["Date"] = "2026-10-01T08:00:00-05:00", ["EventTypeId"] = 30, ["Username"] = "<USER>", ["UserDisplayName"] = "Example Inspector", ["AdditionalInfo"] = null },
            new JObject { ["Date"] = "2026-10-02T09:00:00.1234567-05:00", ["EventTypeId"] = 31, ["Username"] = "<USER>", ["UserDisplayName"] = "Example Inspector", ["AdditionalInfo"] = "Submitting inspection" });
        var attachments = new JArray(new JObject
        {
            ["Id"] = Guid(), ["EditPath"] = @"C:\Temp\work\doc.pdf", ["FileData"] = "JVBERi0xLjQK", ["ReducedFileData"] = null,
            ["FileType"] = "pdf", ["Filename"] = "doc.pdf", ["InspectVersion"] = null, ["AnnotatedPages"] = new JArray(),
            ["IncludedPages"] = new JArray(1), ["ReferenceOnly"] = false, ["LastWriteTime"] = "2026-10-01T08:00:00-05:00",
            ["CanExcludePages"] = false, ["ServerPath"] = @"\\server\share\docs\doc.pdf"
        });
        return new JObject
        {
            ["InspectVersion"] = null, ["WorkOrderId"] = Guid(), ["ServiceId"] = 1, ["FormId"] = 312, ["IsTransfer"] = false,
            ["IsPreinspection"] = false, ["IsApproval"] = false, ["NoCharge"] = false, ["NoChargeReason"] = null,
            ["NextActionId"] = 0, ["NextActionText"] = null, ["InspectionName"] = "Example Inspection",
            ["InspectionNumber"] = number, ["InspectionCode"] = code, ["Attempt"] = 1, ["StatusId"] = 2,
            ["StatusList"] = new JArray(new JObject { ["Id"] = 2, ["Name"] = "Passed" }, new JObject { ["Id"] = 3, ["Name"] = "Failed" }),
            ["PlanNumber"] = "P1", ["BuilderName"] = "Example Builder", ["BuilderAccountId"] = Guid(), ["Contact"] = "Example Contact",
            ["ContactNumber"] = "000-000-0000", ["JobNumber"] = "1234567", ["Project"] = "Example Project",
            ["Address"] = "100 Example St", ["Lot"] = "1", ["Block"] = "A", ["City"] = "Example City", ["State"] = "TX",
            ["InspectorUsername"] = "<USER>", ["InspectorName"] = "Example Inspector", ["InspectorPhone"] = "000-000-0000",
            ["SubmittorUserName"] = null, ["DateInspected"] = "2026-10-13T10:09:17.7656226-05:00", ["TimePeriodInspected"] = "",
            ["StructureTypeId"] = 2, ["CreatedDate"] = "2026-10-01T08:00:00-05:00", ["LastModifiedDate"] = "2026-10-01T08:00:00-05:00",
            ["LastDBDate"] = "2026-10-02T09:00:00.123-05:00", ["Version"] = 1, ["ErrorMessage"] = null,
            ["LastModifiedUsername"] = "<USER>", ["IsReinspection"] = false, ["PurchaseOrderRequired"] = false,
            ["PurchaseOrderNumber"] = "", ["RequisitionNumber"] = null, ["SuperintendentName"] = null, ["Subcontractor"] = null,
            ["ManuallyEnteredAddress"] = false, ["Requests"] = null, ["WhoNotified"] = null, ["WhenNotified"] = null,
            ["Signature"] = null, ["DocumentTypeId"] = Guid(),
            ["InspectionFee_Secure"] = "PLACEHOLDERCIPHERTEXTA0==", ["InspectorOtherFee1_Secure"] = "PLACEHOLDERCIPHERTEXTB0==",
            ["InspectorOtherFee2_Secure"] = "PLACEHOLDERCIPHERTEXTC0==", ["OtherFeeDescription1"] = null,
            ["OtherFeeDescription2"] = null, ["InternalNotes"] = "Example notes",
            ["ElectricProvider"] = null, ["GasProvider"] = null, ["WaterProvider"] = null, ["Rejected"] = false,
            ["Sections"] = sections, ["Events"] = events, ["Attachments"] = attachments, ["ErrorCount"] = 0,
            ["Filename"] = number + ".ins"
        };
    }

    public static JObject Item(JObject document, string number, int occurrence = 0) =>
        Json.Items(document).Where(i => (string?)i["Number"] == number).ElementAt(occurrence);

    public static Item ModelItem(InspectionFile model, string number, int occurrence = 0) =>
        model.Sections.SelectMany(s => s.Items).Where(i => i.Number == number).ElementAt(occurrence);
}

static class Fixtures
{
    private static int _passed, _failed;
    private static readonly List<string> Failures = new();

    private static void Check(string id, bool ok, string what)
    {
        if (ok) { _passed++; Console.WriteLine($"PASS {id} {what}"); }
        else { _failed++; Failures.Add($"{id} {what}"); Console.WriteLine($"FAIL {id} {what}"); }
    }

    private static Exception? Catch(Action action)
    {
        try { action(); return null; } catch (Exception ex) { return ex; }
    }

    public static int Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "red-f002-" + Guid.NewGuid().ToString("N"));
        string inspections = Path.Combine(root, "Inspections");
        string myList = Path.Combine(inspections, "MyList"), review = Path.Combine(inspections, "Review"),
            archive = Path.Combine(inspections, "Archive");
        foreach (var d in new[] { myList, review, archive }) Directory.CreateDirectory(d);
        SurgicalSaveService NewSaver() => new(new FailedSaveRecoveryService(Path.Combine(root, "Recovery")),
            null, Path.Combine(root, "VerifiedSaves"));
        string Place(string folder, string code, JObject document)
        {
            string path = Path.Combine(folder, $"1234567-{code}-1-XX.ins");
            Json.Write(path, document);
            return path;
        }
        bool NoSidecars(string folder) => !Directory.EnumerateFiles(folder).Any(f => f.Contains(".red-"));

        try
        {
            // ---------- A-002-01 Unchanged file is not rewritten ----------
            {
                string path = Place(myList, "CPP", Fixture.Server("CPP"));
                var stamp = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
                File.SetLastWriteTimeUtc(path, stamp);
                byte[] before = File.ReadAllBytes(path);
                var saver = NewSaver();
                var model = saver.Load(path);
                saver.Save(model); // a save request with no edits (forced save / close)
                Check("A-002-01", File.ReadAllBytes(path).SequenceEqual(before) && File.GetLastWriteTimeUtc(path) == stamp && NoSidecars(myList),
                    "open + no-edit save leaves bytes, LastWriteTime and folder untouched");
            }

            // ---------- A-002-02 Forced preserving save is byte-identical (fixtures; corpus mode covers 1684+) ----------
            {
                var server = Fixture.Server("FS");
                var local = Json.Parse(Json.Utf8.GetBytes(InspectProjectionWriter.Project(server).Json));
                var empty = Fixture.Server("FWI"); empty["Sections"] = new JArray(); // E5 forms with no sections
                foreach (var (label, document) in new[] { ("server dialect", server), ("INSPECT-local dialect", local), ("no sections (E5)", empty) })
                {
                    string path = Path.Combine(myList, "fixture-forced.ins");
                    Json.Write(path, document);
                    var saver = NewSaver();
                    var model = saver.Load(path);
                    Check("A-002-02", Json.Utf8.GetBytes(saver.RenderPreservingSave(model)).SequenceEqual(File.ReadAllBytes(path)),
                        $"forced Mode A render is byte-identical ({label})");
                    File.Delete(path);
                }
            }

            // ---------- A-002-03 Preserving save changes only the edited item ----------
            {
                string path = Place(review, "IER", Fixture.Server("IER"));
                var before = Json.ParseFile(path);
                string itemResultId = (string)Fixture.Item(before, "4.2")["ItemResultId"]!;
                var saver = NewSaver();
                var model = saver.Load(path);
                Check("A-002-03", EditorEditService.SetValue(model, Fixture.ModelItem(model, "4.2"), "Fail"), "edit captured");
                saver.Save(model);
                var after = Json.ParseFile(path);
                var diff = Json.Diff(before, after);
                Check("A-002-03", diff.Count == 1 && diff[0] == "$.Sections[3].Items[1].Value" && (string?)Fixture.Item(after, "4.2")["Value"] == "Fail",
                    "only difference is item 4.2 Value (" + string.Join(", ", diff) + ")");
                Check("A-002-03", (string?)Fixture.Item(after, "4.2")["ItemResultId"] == itemResultId, "item 4.2 keeps its ItemResultId");
                Check("A-002-03", before.Properties().Select(p => p.Name).SequenceEqual(after.Properties().Select(p => p.Name)),
                    "no top-level key added or removed (order kept)");
                Check("Q-21", Json.FeeKeys.All(k => Json.Compact(before[k]!) == Json.Compact(after[k]!)),
                    "fee ciphertexts and descriptions byte for byte after a Mode A save");
                File.Delete(path);
            }

            // ---------- A-002-04 Unchanged pictures keep all keys ----------
            {
                string path = Place(myList, "TPC", Fixture.Server("TPC"));
                var before = Json.ParseFile(path);
                var saver = NewSaver();
                var model = saver.Load(path);
                EditorEditService.SetComment(model, Fixture.ModelItem(model, "4.5"), "New note");
                saver.Save(model);
                var after = Json.ParseFile(path);
                var picturesBefore = (JArray)Fixture.Item(before, "4.5")["Pictures"]!;
                var picturesAfter = (JArray)Fixture.Item(after, "4.5")["Pictures"]!;
                Check("A-002-04", picturesAfter.Count == 2 && Enumerable.Range(0, 2).All(i => Json.Compact(picturesBefore[i]) == Json.Compact(picturesAfter[i])),
                    "both picture objects byte-identical, incl. ServerPath, PictureId, Path");
                Check("A-002-04", (string?)Fixture.Item(after, "4.5")["Comments"] == "New note", "the comment edit itself was saved");

                // A changed picture is patched in place: only Description changes, every other key stays.
                Fixture.ModelItem(model, "4.5").Pictures[1].Comment = "second, edited";
                saver.Save(model);
                var patched = (JArray)Fixture.Item(Json.ParseFile(path), "4.5")["Pictures"]!;
                var pd = Json.Diff(picturesAfter, patched);
                Check("A-002-04", pd.Count == 1 && pd[0] == "$[1].Description" && Json.Compact(picturesAfter[0]) == Json.Compact(patched[0]),
                    "editing one picture changes only that picture's Description (" + string.Join(", ", pd) + ")");
                File.Delete(path);
            }

            // ---------- A-002-05 Comments null stays null ----------
            {
                string path = Place(myList, "BF", Fixture.Server("BF"));
                var saver = NewSaver();
                var model = saver.Load(path);
                EditorEditService.SetComment(model, Fixture.ModelItem(model, "4.2"), "edited elsewhere");
                // Opening 4.3's editor with an empty box captures "" over null: still the same empty comment.
                EditorEditService.SetComment(model, Fixture.ModelItem(model, "4.3"), "");
                saver.Save(model);
                var comments = Fixture.Item(Json.ParseFile(path), "4.3")["Comments"]!;
                Check("A-002-05", comments.Type == JTokenType.Null, "item 4.3 still has \"Comments\":null");
                File.Delete(path);
            }

            // ---------- A-002-06 Copy clones source metadata like INSPECT ----------
            {
                string path = Place(myList, "MP", Fixture.Server("MP"));
                var before = Json.ParseFile(path);
                var saver = NewSaver();
                var model = saver.Load(path);
                var section = model.Sections[2];
                var source = Fixture.ModelItem(model, "3.4");
                var copy = SurgicalSaveService.InsertCopy(model, section, source);
                Check("A-002-06", section.Items.IndexOf(copy) == section.Items.IndexOf(source) + 1, "model copy inserted directly after 3.4");
                saver.Save(model);
                var after = Json.ParseFile(path);
                var items = (JArray)after["Sections"]![2]!["Items"]!;
                var sourceJson = (JObject)items[0]; var copyJson = (JObject)items[1];
                Check("A-002-06", (string?)copyJson["Number"] == "3.4" && (bool?)copyJson["IsCopied"] == true &&
                    (int?)copyJson["PictureSizeId"] == 2 && Json.Compact(copyJson["ValueList"]!) == "[\"A\",\"B\"]" &&
                    (bool?)copyJson["IsPictureRequired"] == false && copyJson["Value"]!.Type == JTokenType.Null &&
                    Json.Compact(copyJson["Pictures"]!) == "[]" && copyJson["ItemResultId"] == null &&
                    copyJson["Template"]!.Type == JTokenType.Null && (int?)copyJson["ResultSortOrder"] == 1,
                    "copy: IsCopied true, PictureSizeId 2, ValueList [A,B], IsPictureRequired false, Value null, Pictures [], no ItemResultId");
                Check("A-002-06", Json.Compact(sourceJson) == Json.Compact(Fixture.Item(before, "3.4")), "source item unchanged");
                var copyKeys = copyJson.Properties().Select(p => p.Name).ToList();
                var sourceKeys = sourceJson.Properties().Select(p => p.Name).Where(k => k != "ItemResultId").ToList();
                Check("A-002-06", copyKeys.SequenceEqual(sourceKeys), "copy keeps the source item's keys and order (minus ItemResultId)");
                File.Delete(path);
            }

            // ---------- A-002-07 ResultSortOrder never collides ----------
            {
                string path = Place(myList, "HEF", Fixture.Server("HEF"));
                var before = Json.ParseFile(path);
                string copyBId = (string)Fixture.Item(before, "5.1", 2)["ItemResultId"]!;
                var saver = NewSaver();
                var model = saver.Load(path);
                var section = model.Sections[4];
                section.Items.Remove(section.Items[1]);                      // remove copy A
                SurgicalSaveService.InsertCopy(model, section, section.Items[0]); // add a new copy of the original
                saver.Save(model);
                var items = ((JArray)Json.ParseFile(path)["Sections"]![4]!["Items"]!).Cast<JObject>().ToList();
                Check("A-002-07", items.Count == 3 && items.Select(i => (int)i["ResultSortOrder"]!).SequenceEqual(new[] { 0, 1, 2 }),
                    "three consecutive 5.1 items have ResultSortOrder 0, 1, 2");
                Check("A-002-07", (string?)items[0]["Comments"] == "original" && items[1]["Comments"]!.Type == JTokenType.Null &&
                    (string?)items[2]["Comments"] == "copy B text" && (string?)items[2]["ItemResultId"] == copyBId && items[1]["ItemResultId"] == null,
                    "items matched by identity: copy B keeps its text and ItemResultId; the new copy is blank");
                File.Delete(path);
            }

            // ---------- A-002-08 No key injection ----------
            {
                var document = Fixture.Server("SRP");
                Fixture.Item(document, "2.1").Remove("ItemResultId");
                document.Remove("Filename");
                string path = Place(myList, "SRP", document);
                var saver = NewSaver();
                var model = saver.Load(path);
                EditorEditService.SetValue(model, Fixture.ModelItem(model, "2.1"), "Fail");
                saver.Save(model);
                var after = Json.ParseFile(path);
                Check("A-002-08", Fixture.Item(after, "2.1")["ItemResultId"] == null && after["Filename"] == null &&
                    (string?)Fixture.Item(after, "2.1")["Value"] == "Fail",
                    "no ItemResultId on 2.1 and no top-level Filename after an edit and save");
                File.Delete(path);
            }

            // ---------- A-002-09 Other folders' copies untouched ----------
            {
                string mine = Place(myList, "ME", Fixture.Server("ME"));
                var other = Fixture.Server("ME"); other["InternalNotes"] = "different bytes";
                string archived = Place(archive, "ME", other);
                var stamp = new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc);
                File.SetLastWriteTimeUtc(archived, stamp);
                byte[] archivedBytes = File.ReadAllBytes(archived);
                var saver = NewSaver();
                var model = saver.Load(mine);
                EditorEditService.SetComment(model, Fixture.ModelItem(model, "4.2"), "edited in My List");
                saver.Save(model);
                Check("A-002-09", File.ReadAllBytes(archived).SequenceEqual(archivedBytes) && File.GetLastWriteTimeUtc(archived) == stamp &&
                    (string?)Fixture.Item(Json.ParseFile(mine), "4.2")["Comments"] == "edited in My List",
                    "Archive copy's bytes and LastWriteTime unchanged; only the open My List file was written");
                File.Delete(mine); File.Delete(archived);
            }

            // ---------- A-002-10 Projection reproduces INSPECT's own files (fixture; corpus mode uses the real Temp snapshots) ----------
            {
                var local = InspectProjectionWriter.Project(Fixture.Server("CPR")).Json;
                var again = InspectProjectionWriter.Project(Json.Parse(Json.Utf8.GetBytes(local))).Json;
                Check("A-002-10", again == local, "projecting an INSPECT-local document reproduces it byte for byte");
            }

            // ---------- A-002-11 Projection key order ----------
            {
                var server = Fixture.Server("PLY");
                server["RedPlanCheck"] = new JObject { ["review"] = "pending" };
                Check("A-002-11", server.Count == 70, "fixture has the 69 server keys plus RedPlanCheck");
                var projection = InspectProjectionWriter.Project(server);
                var projected = Json.Parse(projection.ToUtf8Bytes());
                Check("A-002-11", projected.Properties().Select(p => p.Name).SequenceEqual(InspectProjectionWriter.TopLevelKeys) &&
                    InspectProjectionWriter.TopLevelKeys.Count == 63, "exactly the 63 SPEC B1 top-level keys, in order");
                Check("A-002-11", Json.ServerOnlyTopLevel.All(k => projected[k] == null), "no provider, Rejected, ErrorCount, Filename or RedPlanCheck key");
                Check("A-002-11", Json.Items(projected).All(i => i["ItemResultId"] == null) &&
                    Json.Pictures(projected).All(p => p["ServerPath"] == null && p["PictureId"] == null) &&
                    ((JArray)projected["Attachments"]!).All(a => a["ServerPath"] == null && a["ReducedFileData"] == null),
                    "no item ItemResultId, picture ServerPath/PictureId or attachment ServerPath");
                Check("A-002-11", ((JArray)projected["Sections"]!).Cast<JObject>().All(s => s.Properties().Select(p => p.Name).SequenceEqual(InspectProjectionWriter.SectionKeys)) &&
                    Json.Items(projected).All(i => i.Properties().Select(p => p.Name).SequenceEqual(InspectProjectionWriter.ItemKeys)) &&
                    Json.Pictures(projected).All(p => p.Properties().Select(x => x.Name).SequenceEqual(InspectProjectionWriter.PictureKeys)) &&
                    ((JArray)projected["Events"]!).Cast<JObject>().All(e => e.Properties().Select(p => p.Name).SequenceEqual(InspectProjectionWriter.EventKeys)) &&
                    ((JArray)projected["Attachments"]!).Cast<JObject>().All(a => a.Properties().Select(p => p.Name).SequenceEqual(InspectProjectionWriter.AttachmentKeys)),
                    "section (6), item (29), picture (4), event (5) and attachment (11) key lists and order");
                Check("A-002-11", projection.Warnings.Count == 0, "no projection warnings for a well-formed server file");
                Check("Q-21", Json.FeeKeys.All(k => Json.Compact(server[k]!) == Json.Compact(projected[k]!)), "fee fields byte for byte in Mode B");
                Check("B5", (string?)projected["DateInspected"] == "2026-10-13T10:09:17.7656226-05:00" &&
                    (string?)projected["LastDBDate"] == "2026-10-02T09:00:00.123-05:00", "unchanged dates written verbatim");
                Check("B3", (int?)projected["Sections"]![0]!["SortOrder"] == 0, "absent Section.SortOrder written as 0");
            }

            // ---------- A-002-12 DisplayLabel recomputed ----------
            {
                var projected = Json.Parse(InspectProjectionWriter.Project(Fixture.Server("PPE")).ToUtf8Bytes());
                Check("A-002-12", (string?)Fixture.Item(projected, "4.12")["DisplayLabel"] == "4.12 Anchor bolts *" &&
                    (string?)Fixture.Item(projected, "4.2")["DisplayLabel"] == "4.2 Rebar", "markup label becomes \"4.12 Anchor bolts *\"");
            }

            // ---------- A-002-13 Encoding ----------
            {
                var document = Fixture.Server("TFF");
                Fixture.Item(document, "4.2")["Comments"] = "Slab <OK> & \"dry\"/clean\r\nsecond line é";
                string path = Path.Combine(root, "projection.ins");
                File.WriteAllBytes(path, InspectProjectionWriter.Project(document).ToUtf8Bytes());
                byte[] bytes = File.ReadAllBytes(path);
                string text = Encoding.UTF8.GetString(bytes);
                Check("A-002-13", bytes[0] == (byte)'{' && bytes[^1] == (byte)'}', "starts with { (no BOM) and ends with } (no newline)");
                Check("A-002-13", text.Contains("Slab <OK> & \\\"dry\\\"/clean") && text.Contains("\\r\\n") && text.Contains("second line é") && !text.Contains("\\u"),
                    "raw < > & / and non-ASCII, escaped quotes, \\r\\n, no \\u escapes");
            }

            // ---------- A-002-14 Busy file ----------
            {
                string path = Place(myList, "HET", Fixture.Server("HET"));
                byte[] before = File.ReadAllBytes(path);
                var hold = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                var release = Task.Delay(500).ContinueWith(_ => hold.Dispose());
                var watch = Stopwatch.StartNew();
                var error = Catch(() => NewSaver().Load(path));
                release.Wait();
                Check("A-002-14", error == null && watch.ElapsedMilliseconds >= 450, $"lock held 0.5 s: retried and opened after release ({watch.ElapsedMilliseconds} ms)");
                hold = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                release = Task.Delay(2000).ContinueWith(_ => hold.Dispose());
                error = Catch(() => NewSaver().Load(path));
                release.Wait();
                Check("A-002-14", error is InspectionFileBusyException && error.Message == InspectionFileReader.BusyMessage &&
                    File.ReadAllBytes(path).SequenceEqual(before), "lock held 2 s: busy message after 100/200/400 ms retries, file unchanged");
                File.Delete(path);
            }

            // ---------- A-002-15 Damaged file ----------
            {
                foreach (var (label, bytes) in new[] { ("2048 NUL bytes", new byte[2048]), ("empty", Array.Empty<byte>()),
                    ("truncated JSON", Encoding.UTF8.GetBytes("{\"InspectVersion\":null,\"Sections\":[")), ("not an object", Encoding.UTF8.GetBytes("[1,2]")) })
                {
                    string path = Path.Combine(myList, "1234567-PLY-1-XX.ins");
                    File.WriteAllBytes(path, bytes);
                    var error = Catch(() => NewSaver().Load(path));
                    Check("A-002-15", error is InspectionFileDamagedException && error.Message == "This inspection file is damaged and cannot be opened." &&
                        File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(bytes), $"{label}: damaged message, file kept with unchanged bytes");
                    File.Delete(path);
                }
            }

            // ---------- A-002-16 ResultSortOrder runs across section boundaries ----------
            {
                var document = Fixture.Server("STR");
                Fixture.Item(document, "4.1")["ResultSortOrder"] = 0; // as if a section-local rule had written it
                string path = Place(myList, "STR", document);
                var saver = NewSaver();
                var model = saver.Load(path);
                EditorEditService.SetComment(model, Fixture.ModelItem(model, "4.2"), "any edit");
                saver.Save(model);
                Check("A-002-16", (int?)Fixture.Item(Json.ParseFile(path), "4.1")["ResultSortOrder"] == 1,
                    "section 4's first item (same ItemId as section 3's last) gets ResultSortOrder 1");
                File.Delete(path);
            }

            // ---------- A-002-17 File changed on disk while open ----------
            {
                string path = Place(review, "HET", Fixture.Server("HET"));
                var saver = NewSaver();
                var model = saver.Load(path);
                // RED has no Internal Notes editor yet (F-007/F-011): an unsaved comment edit stands in for it.
                EditorEditService.SetComment(model, Fixture.ModelItem(model, "4.2"), "unsaved RED edit");
                // Another computer's fee automation replaces the fee ciphertext (Dropbox sync).
                string external = File.ReadAllText(path).Replace("PLACEHOLDERCIPHERTEXTA0==", "EXTERNALCIPHERTEXTZZ9==");
                File.WriteAllBytes(path, Json.Utf8.GetBytes(external));
                byte[] externalBytes = File.ReadAllBytes(path);
                var saveError = Catch(() => saver.Save(model));
                Check("A-002-17", saveError is InspectionChangedOnDiskException { TargetMissing: false } &&
                    File.ReadAllBytes(path).SequenceEqual(externalBytes), "save (manual or autosave path) refused; newer file untouched");
                Check("A-002-17", NoSidecars(review), "no temp or backup file created in Review (A10)");
                Check("A-002-17", Catch(() => saver.EnsureUnchangedOnDisk()) is InspectionChangedOnDiskException,
                    "pre-write check for Submit refuses too");
                string copy = Path.Combine(root, "copies", "1234567-HET-1-XX RED copy.ins");
                Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
                saver.SaveCopy(model, copy);
                Check("A-002-17", File.ReadAllBytes(path).SequenceEqual(externalBytes) &&
                    (string?)Fixture.Item(Json.ParseFile(copy), "4.2")["Comments"] == "unsaved RED edit" &&
                    (string?)Json.ParseFile(copy)["InspectionFee_Secure"] == "PLACEHOLDERCIPHERTEXTA0==" &&
                    Catch(() => saver.Save(model)) is InspectionChangedOnDiskException,
                    "Save As Copy writes the edits elsewhere; the report stays protected");
                var reloaded = saver.Load(path); // Reload
                string fee = (string)Json.ParseFile(path)["InspectionFee_Secure"]!;
                Check("A-002-17", fee == "EXTERNALCIPHERTEXTZZ9==" && File.ReadAllBytes(path).SequenceEqual(externalBytes) &&
                    reloaded.ExtensionData!["InspectionFee_Secure"]!.ToString() == "EXTERNALCIPHERTEXTZZ9==",
                    "after Reload, InspectionFee_Secure is the externally written value byte for byte");
                // The report moves away while open (e.g. INSPECT submitted it to Outbox): never recreated.
                var moved = NewSaver();
                var movedModel = moved.Load(path);
                File.Delete(path);
                moved.SetResult(3, 0, null);
                Check("E1", Catch(() => moved.Save(movedModel)) is InspectionChangedOnDiskException { TargetMissing: true } &&
                    Catch(() => moved.EnsureUnchangedOnDisk()) is InspectionChangedOnDiskException { TargetMissing: true } &&
                    !File.Exists(path) && NoSidecars(review), "a report moved away while open is never recreated");
            }

            // ---------- A-002-18 Confirmed overwrite after a changed-on-disk stop (owner decision 2026-10-18) ----------
            {
                string path = Place(myList, "IEF", Fixture.Server("IEF"));
                var saver = NewSaver();
                var model = saver.Load(path);
                EditorEditService.SetComment(model, Fixture.ModelItem(model, "4.2"), "RED edit kept by overwrite");
                // Outside RED: INSPECT changes Internal Notes and fee automation replaces a fee ciphertext.
                string external = File.ReadAllText(path)
                    .Replace("\"InternalNotes\":\"Example notes\"", "\"InternalNotes\":\"changed outside RED\"")
                    .Replace("PLACEHOLDERCIPHERTEXTB0==", "EXTERNALCIPHERTEXTYY8==");
                File.WriteAllBytes(path, Json.Utf8.GetBytes(external));
                Check("A-002-18", Catch(() => saver.Save(model)) is InspectionChangedOnDiskException, "an ordinary save still stops first");
                string keptCopy = saver.OverwriteChangedFile(model);
                var after = Json.ParseFile(path);
                Check("A-002-18", (string?)Fixture.Item(after, "4.2")["Comments"] == "RED edit kept by overwrite" &&
                    (string?)after["InternalNotes"] == "Example notes",
                    "overwrite writes RED's version (the outside Internal Notes change is replaced)");
                Check("A-002-18", (string?)after["InspectorOtherFee1_Secure"] == "EXTERNALCIPHERTEXTYY8==" &&
                    (string?)after["InspectionFee_Secure"] == "PLACEHOLDERCIPHERTEXTA0==",
                    "fee fields are kept from the newer file byte for byte (Q-21)");
                EditorEditService.SetComment(model, Fixture.ModelItem(model, "4.2"), "next ordinary save");
                saver.Save(model);
                Check("A-002-18", (string?)Fixture.Item(Json.ParseFile(path), "4.2")["Comments"] == "next ordinary save" &&
                    (string?)Json.ParseFile(path)["InspectorOtherFee1_Secure"] == "EXTERNALCIPHERTEXTYY8==",
                    "after the overwrite, ordinary saves continue against the new file");
                // The replaced newer file is kept in RED's local Recovery folder, untouched by the backup trimming of
                // later saves (the save's own backup keeps only the 3 newest).
                for (int i = 0; i < 4; i++)
                {
                    // The last save leaves the text the race check below edits from outside.
                    EditorEditService.SetComment(model, Fixture.ModelItem(model, "4.2"), i < 3 ? $"later save {i}" : "next ordinary save");
                    saver.Save(model);
                }
                // While saving is paused: one rolling recovery copy, replaced as the edits change (never one per edit).
                var rollingSaver = NewSaver();
                var rollingModel = rollingSaver.Load(path);
                string firstRolling = "", lastRolling = "";
                for (int i = 0; i < 5; i++)
                {
                    EditorEditService.SetComment(rollingModel, Fixture.ModelItem(rollingModel, "4.2"), $"paused edit {i}");
                    lastRolling = rollingSaver.PreserveUnsavedEditsRolling(rollingModel);
                    if (i == 0) firstRolling = lastRolling;
                }
                string recoveryDir = Path.GetDirectoryName(lastRolling)!;
                Check("A-002-18", Directory.GetFiles(recoveryDir, "paused-edits-*.ins").Length == 1 && !File.Exists(firstRolling) &&
                    File.ReadAllText(lastRolling).Contains("paused edit 4"),
                    "while saving is paused, one rolling recovery copy is kept and replaced (not one per edit)");
                Check("A-002-18", Path.GetFileName(keptCopy).StartsWith("replaced-by-overwrite-") &&
                    !keptCopy.StartsWith(inspections, StringComparison.OrdinalIgnoreCase) &&
                    File.ReadAllBytes(keptCopy).SequenceEqual(Json.Utf8.GetBytes(external)),
                    "the replaced newer file is kept byte for byte outside the Inspections tree, even after 4 more saves");

                // The file changes again between the overwrite's read and its replacement: nothing is written.
                File.WriteAllBytes(path, Json.Utf8.GetBytes(File.ReadAllText(path).Replace("next ordinary save", "outside again")));
                bool raced = false;
                var racing = new SurgicalSaveService(new FailedSaveRecoveryService(Path.Combine(root, "Recovery")), (p, json, expected) =>
                {
                    if (!raced) { raced = true; File.WriteAllBytes(p, Json.Utf8.GetBytes(File.ReadAllText(p).Replace("outside once more", "outside a third time"))); }
                    return AtomicInspectionWriter.Write(p, json, expected);
                }, Path.Combine(root, "VerifiedSaves"));
                var racingModel = racing.Load(path);
                File.WriteAllBytes(path, Json.Utf8.GetBytes(File.ReadAllText(path).Replace("outside again", "outside once more")));
                EditorEditService.SetComment(racingModel, Fixture.ModelItem(racingModel, "4.2"), "racing edit");
                var raceError = Catch(() => racing.OverwriteChangedFile(racingModel));
                Check("A-002-18", raceError is InspectionChangedOnDiskException && File.ReadAllText(path).Contains("outside a third time") &&
                    !File.ReadAllText(path).Contains("racing edit"), "a further change during the overwrite is still refused");
                File.Delete(path);

                // A moved report is never recreated by an overwrite.
                string moved = Place(myList, "IAP", Fixture.Server("IAP"));
                var movedSaver = NewSaver();
                var movedModel = movedSaver.Load(moved);
                File.Delete(moved);
                Check("A-002-18", Catch(() => movedSaver.OverwriteChangedFile(movedModel)) is InspectionChangedOnDiskException { TargetMissing: true } &&
                    !File.Exists(moved), "overwrite never recreates a moved or deleted report");
            }

            // ---------- SPEC R6 empty picture shells ----------
            {
                string path = Place(myList, "AFI", Fixture.Server("AFI"));
                var saver = NewSaver();
                var model = saver.Load(path);
                var beams = Fixture.ModelItem(model, "4.6");
                Check("R6", beams.Pictures.Count == 1, "shell hidden from the model/UI");
                EditorEditService.SetComment(model, Fixture.ModelItem(model, "4.2"), "edit elsewhere");
                saver.Save(model);
                var kept = (JArray)Fixture.Item(Json.ParseFile(path), "4.6")["Pictures"]!;
                Check("R6", kept.Count == 2 && kept[0]["Image"]!.Type == JTokenType.Null, "shell kept in the file while the item's pictures are untouched");
                beams.Pictures.Add(new Picture { Data = Fixture.FakeJpeg(9), Title = "Beams", Comment = "new photo" });
                saver.Save(model);
                var rebuilt = (JArray)Fixture.Item(Json.ParseFile(path), "4.6")["Pictures"]!;
                Check("R6", rebuilt.Count == 2 && Json.Compact(rebuilt[0]) == Json.Compact(kept[1]) &&
                    ((JObject)rebuilt[1]).Properties().Select(p => p.Name).SequenceEqual(new[] { "Path", "Caption", "Description", "Image" }),
                    "after a picture edit the shell is dropped, the old picture stays verbatim, the new one has INSPECT's 4 keys");
                File.Delete(path);
            }

            // ---------- A10: sidecars never beside Outbox/Archive/Review/Temp reports; My List keeps them ----------
            {
                string Staged(string relative) => AtomicInspectionWriter.SidecarDirectory(Path.Combine(inspections, relative));
                string redStaging = Path.Combine(myList, ".red-save-staging");
                Check("A10", Staged(@"MyList\x.ins") == myList &&
                    Staged(@"Review\x.ins") == Path.Combine(redStaging, "Review") &&
                    Staged(@"Archive\x.ins") == Path.Combine(redStaging, "Archive") &&
                    Staged(@"Outbox\x.ins") == Path.Combine(redStaging, "Outbox") &&
                    Staged(@"Temp\10-13-2026\x_v01.ins") == Path.Combine(redStaging, "Temp") &&
                    (File.GetAttributes(redStaging) & FileAttributes.Hidden) != 0,
                    "Review, Archive, Outbox and Temp\\{date} stage in the same tree's hidden MyList\\.red-save-staging (same volume, per tree)");
                string reviewPath = Place(review, "TRSI", Fixture.Server("TRSI"));
                var saver = NewSaver();
                var model = saver.Load(reviewPath);
                EditorEditService.SetComment(model, Fixture.ModelItem(model, "4.2"), "super user edit");
                saver.Save(model);
                Check("A10", NoSidecars(review) && (string?)Fixture.Item(Json.ParseFile(reviewPath), "4.2")["Comments"] == "super user edit",
                    "a successful save of a Review report leaves no temp or backup file in Review");
                File.Delete(reviewPath);

                // A report whose folder (or drive) is gone is unavailable, not moved: a plain retryable failure (E1 keeps
                // "moved" for a report missing from a folder that still exists).
                string gone = Path.Combine(root, "UnpluggedDrive", "Review");
                Directory.CreateDirectory(gone);
                string gonePath = Path.Combine(gone, Path.GetFileName(Place(review, "TRSI", Fixture.Server("TRSI"))));
                File.Move(Path.Combine(review, Path.GetFileName(gonePath)), gonePath);
                var goneSaver = NewSaver();
                var goneModel = goneSaver.Load(gonePath);
                EditorEditService.SetComment(goneModel, Fixture.ModelItem(goneModel, "4.2"), "edit while the drive is gone");
                Directory.Delete(Path.Combine(root, "UnpluggedDrive"), recursive: true);
                var goneError = Catch(() => goneSaver.Save(goneModel));
                Check("E1", goneError is IOException && goneError is not InspectionChangedOnDiskException &&
                    !Directory.Exists(gone), "a missing folder or drive is a retryable failure, not 'moved', and nothing is recreated");
            }

            // ---------- Post-replace check tolerates a sync client's brief lock ----------
            {
                string path = Place(myList, "TRDI", Fixture.Server("TRDI"));
                byte[] before = File.ReadAllBytes(path);
                string json = Encoding.UTF8.GetString(before).Replace("Example notes", "Edited notes");
                bool replaced = false; int locked = 0;
                var ops = new AtomicInspectionWriter.Operations
                {
                    Replace = (temp, target, backup) => { File.Replace(temp, target, backup); replaced = true; },
                    ReadAllBytes = p => replaced && locked++ == 0 && p == Path.GetFullPath(path)
                        ? throw new IOException("sharing violation", unchecked((int)0x80070020)) : File.ReadAllBytes(p),
                    Delay = _ => { }
                };
                var written = AtomicInspectionWriter.Write(path, json, before, ops);
                Check("A1", written.SequenceEqual(File.ReadAllBytes(path)) && locked >= 2, "a sharing violation on the post-replace read is retried, not reported as failure");
                File.Delete(path);
            }

            // ---------- Copies placed after a run keep consecutive ResultSortOrder (trade summary rule) ----------
            {
                string path = Place(myList, "FSF", Fixture.Server("FSF"));
                var saver = NewSaver();
                var model = saver.Load(path);
                var section = model.Sections[4];
                var original = section.Items[0];
                var summary = new Item { ItemId = original.ItemId, Number = original.Number, Name = original.Name, IsCopied = true,
                    CopiedFrom = original, Comments = "Summary for test", Pictures = new List<Picture>() };
                int insertAt = section.Items.IndexOf(original) + 1;
                while (insertAt < section.Items.Count && section.Items[insertAt].ItemId == original.ItemId) insertAt++;
                section.Items.Insert(insertAt, summary);
                saver.Save(model);
                var items = ((JArray)Json.ParseFile(path)["Sections"]![4]!["Items"]!).Cast<JObject>().ToList();
                Check("A5", items.Count == 4 && items.Select(i => (int)i["ResultSortOrder"]!).SequenceEqual(new[] { 0, 1, 2, 3 }) &&
                    (string?)items[3]["Comments"] == "Summary for test" && items[3]["ItemResultId"] == null,
                    "a summary copy appended to its source's run gets the next ResultSortOrder");
                File.Delete(path);
            }

            // ---------- Robustness: null Items, null item entries and null Pictures round-trip ----------
            {
                string path = Path.Combine(myList, "1234567-SWI-1-XX.ins");
                const string text = "{\"Sections\":[{\"Items\":null},{\"Items\":[null,{\"ItemId\":1,\"Pictures\":null,\"Value\":\"a\"}]}],\"Events\":null}";
                File.WriteAllBytes(path, Json.Utf8.GetBytes(text));
                var saver = NewSaver();
                var model = saver.Load(path);
                bool identical = saver.RenderPreservingSave(model) == text;
                EditorEditService.SetValue(model, model.Sections[1].Items[1], "b");
                saver.Save(model);
                Check("R3", identical && File.ReadAllText(path) == text.Replace("\"Value\":\"a\"", "\"Value\":\"b\""),
                    "null Items, null item entries and null Pictures survive load, forced render and an edit");
                File.Delete(path);
            }

            // ---------- SPEC E4 / B3 / B5 projection details ----------
            {
                var projected = Json.Parse(InspectProjectionWriter.Project(Fixture.Server("SCI")).ToUtf8Bytes());
                Check("E4", projected["Sections"]![2]!["ControlName"]!.Type == JTokenType.Null, "section ControlName null round-trips as null");
                var minimal = JObject.Parse("{\"Sections\":[{\"Items\":[{\"Pictures\":[{}]}]}],\"Attachments\":[{}],\"Events\":[{}]}");
                var defaults = Json.Parse(InspectProjectionWriter.Project(minimal).ToUtf8Bytes());
                var item = (JObject)defaults["Sections"]![0]!["Items"]![0]!;
                Check("B3", (string?)defaults["WorkOrderId"] == "00000000-0000-0000-0000-000000000000" &&
                    defaults["BuilderAccountId"]!.Type == JTokenType.Null && defaults["IsReinspection"]!.Type == JTokenType.Null &&
                    (string?)defaults["CreatedDate"] == "0001-01-01T00:00:00" && defaults["LastDBDate"]!.Type == JTokenType.Null &&
                    Json.Compact(defaults["StatusList"]!) == "[]" && (int?)defaults["StatusId"] == 0 && (bool?)defaults["IsApproval"] == false,
                    "absent top-level keys get INSPECT model defaults (Guid, Guid?, bool?, DateTime, DateTime?, lists)");
                Check("B3", (bool?)item["Required"] == true && (string?)item["ControlName"] == "ItemStandardControl" &&
                    item["ValueList"]!.Type == JTokenType.Null && Json.Compact(item["CommentList"]!) == "[]" &&
                    item["MaxIncidents"]!.Type == JTokenType.Null && (string?)item["DisplayLabel"] == "  *",
                    "absent item keys: Required true, ControlName ItemStandardControl, ValueList null, CommentList []");
                var attachment = (JObject)defaults["Attachments"]![0]!;
                Check("B3", (string?)attachment["Id"] == "00000000-0000-0000-0000-000000000000" && Json.Compact(attachment["AnnotatedPages"]!) == "[]" &&
                    (string?)attachment["LastWriteTime"] == "0001-01-01T00:00:00" && (string?)defaults["Events"]![0]!["Date"] == "0001-01-01T00:00:00",
                    "absent attachment and event keys get model defaults");
                var odd = Fixture.Server("COH");
                odd["WorkOrderId"] = ((string)odd["WorkOrderId"]!).ToUpperInvariant();
                odd["PlanNumber"] = true; odd["Lot"] = 12; odd["Attempt"] = "2";
                var coerced = Json.Parse(InspectProjectionWriter.Project(odd).ToUtf8Bytes());
                Check("B3", (string?)coerced["PlanNumber"] == "true" && (string?)coerced["Lot"] == "12" && (int?)coerced["Attempt"] == 2,
                    "wrong-type values converted as Newtonsoft's reader does (true -> \"true\", 12 -> \"12\", \"2\" -> 2)");
                odd["PlanNumber"] = "P1"; odd["Lot"] = "1"; odd["Attempt"] = 1;
                Fixture.Item(odd, "4.5")["Pictures"]![0]!["Image"] = "/9j/4AAQ\r\nSkZJRg==";
                var normalized = Json.Parse(InspectProjectionWriter.Project(odd).ToUtf8Bytes());
                Check("B5", (string?)normalized["WorkOrderId"] == ((string)odd["WorkOrderId"]!).ToLowerInvariant() &&
                    (string?)Fixture.Item(normalized, "4.5")["Pictures"]![0]!["Image"] == "/9j/4AAQSkZJRg==",
                    "GUIDs lowercase, byte[] as standard base64 without line breaks");
                var signed = new DateTime(2026, 10, 13, 10, 9, 17, DateTimeKind.Local);
                Check("B5", Regex.IsMatch(InspectProjectionWriter.FormatInspectDate(signed.AddTicks(7656226)), @"^2026-10-13T10:09:17\.7656226[+-]\d\d:\d\d$") &&
                    Regex.IsMatch(InspectProjectionWriter.FormatInspectDate(signed.AddTicks(5000000)), @"^2026-10-13T10:09:17\.5[+-]\d\d:\d\d$") &&
                    Regex.IsMatch(InspectProjectionWriter.FormatInspectDate(signed), @"^2026-10-13T10:09:17[+-]\d\d:\d\d$"),
                    "dates RED sets: local with offset, fraction trimmed");
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
        Console.WriteLine($"{_passed} passed, {_failed} failed");
        foreach (var failure in Failures) Console.WriteLine("  FAILED " + failure);
        return _failed == 0 ? 0 : 1;
    }
}

static class Corpus
{
    private sealed record Entry(string Folder, int Index, string Path, string Code);

    public static int Run(string sandboxRoot, string? reportPath)
    {
        string full = Path.GetFullPath(sandboxRoot);
        if (full.Contains(@"\Dropbox\Inspections", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("Refusing to run against the live Dropbox\\Inspections tree. Copy it to a sandbox first.");
            return 2;
        }
        string work = Path.Combine(Path.GetDirectoryName(full)!, "f002-work");
        Directory.CreateDirectory(work);
        var entries = new List<Entry>();
        foreach (string folder in new[] { "MyList", "Review", "Archive", "Temp" })
        {
            string dir = Path.Combine(full, folder);
            if (!Directory.Exists(dir)) continue;
            var files = Directory.GetFiles(dir, "*.ins", folder == "Temp" ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
            for (int i = 0; i < files.Length; i++)
            {
                string name = Path.GetFileNameWithoutExtension(files[i]);
                var parts = name.Split('-');
                entries.Add(new Entry(folder, i, files[i], parts.Length > 1 ? parts[1] : "?"));
            }
        }

        var report = new StringBuilder();
        int total = 0, identical = 0, unchangedNoWrite = 0, keyOrderOk = 0, droppedOk = 0, feesOk = 0, warnings = 0, loadErrors = 0;
        int naRewriteFiles = 0;
        int localTemp = 0, localTempIdentical = 0;
        var modeADiffs = new Dictionary<string, int>();
        var modeAFailures = new List<string>();
        var v3Failures = new List<string>();
        var localTempFiles = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var serverTempFiles = new List<Entry>();
        var watch = Stopwatch.StartNew();

        foreach (var entry in entries)
        {
            total++;
            string id = $"{entry.Folder}#{entry.Index} ({entry.Code})";
            try
            {
                byte[] bytes = File.ReadAllBytes(entry.Path);
                var original = Json.Parse(bytes);
                bool isLocal = original.Properties().Select(p => p.Name).SequenceEqual(InspectProjectionWriter.TopLevelKeys);
                var saver = new SurgicalSaveService(new FailedSaveRecoveryService(Path.Combine(work, "Recovery")), null, Path.Combine(work, "VerifiedSaves"));
                var model = saver.Load(entry.Path);
                // F-004 V1: the open-time NA→NI rewrite is held back until another change, so a no-edit render is the
                // deferred one; the render with NI is what an edited save and Submit carry.
                byte[] unedited = Json.Utf8.GetBytes(saver.RenderPreservingSave(model, deferNaRewrites: true));
                byte[] render = Json.Utf8.GetBytes(saver.RenderPreservingSave(model));

                // A-002-02: forced Mode A save with no edits is byte-identical.
                if (unedited.SequenceEqual(bytes)) identical++;
                else
                {
                    var diff = Json.Diff(original, Json.Parse(unedited)).Select(Json.Normalize).Distinct().ToList();
                    foreach (var path in diff) modeADiffs[path] = modeADiffs.GetValueOrDefault(path) + 1;
                    modeAFailures.Add($"{id}: {string.Join(", ", diff.Take(4))}{(diff.Count > 4 ? ", …" : "")}");
                }
                // F-004 V1: with NI, only the Value of items stored as exactly "NA" may differ.
                if (!render.SequenceEqual(bytes))
                {
                    var rendered = Json.Parse(render);
                    var naDiffs = Json.Diff(original, rendered);
                    bool onlyNa = naDiffs.All(p => p.EndsWith(".Value") && (string?)original.SelectToken(p) == "NA" && (string?)rendered.SelectToken(p) == "NI");
                    if (onlyNa) naRewriteFiles++;
                    else modeAFailures.Add($"{id}: NI render differs outside NA values: {string.Join(", ", naDiffs.Select(Json.Normalize).Distinct().Take(4))}");
                }

                // Mode B over the Mode A document (what Submit does: save, then project).
                var projection = InspectProjectionWriter.Project(Json.Parse(render));
                warnings += projection.Warnings.Count;
                var projected = Json.Parse(projection.ToUtf8Bytes());
                // V5: top-level key order; dropped keys absent.
                if (projected.Properties().Select(p => p.Name).SequenceEqual(InspectProjectionWriter.TopLevelKeys)) keyOrderOk++;
                if (Json.ServerOnlyTopLevel.All(k => projected[k] == null) && Json.Items(projected).All(i => i["ItemResultId"] == null) &&
                    Json.Pictures(projected).All(p => p["ServerPath"] == null && p["PictureId"] == null)) droppedOk++;
                // Q-21: fee fields pass through byte for byte (projection compared with the original file).
                if (Json.FeeKeys.All(k => original[k] == null ? projected[k]!.Type == JTokenType.Null : Json.Compact(original[k]!) == Json.Compact(projected[k]!)))
                    feesOk++;

                if (entry.Folder == "Temp")
                {
                    if (isLocal)
                    {
                        // V3 / A-002-10: INSPECT's own local-dialect snapshots must be reproduced byte for byte.
                        localTemp++;
                        localTempFiles[entry.Path] = bytes;
                        byte[] projectedBytes = projection.ToUtf8Bytes();
                        if (projectedBytes.SequenceEqual(bytes)) localTempIdentical++;
                        else v3Failures.Add($"{id}: {string.Join(", ", Json.Diff(original, projected).Select(Json.Normalize).Distinct().Take(6))}");
                    }
                    else serverTempFiles.Add(entry);
                }

                // A-002-01: a save request with no edits writes nothing, NA files included (F-004 V1 defers the NI).
                if (unedited.SequenceEqual(bytes))
                {
                    var stamp = File.GetLastWriteTimeUtc(entry.Path);
                    saver.Save(model);
                    if (File.ReadAllBytes(entry.Path).SequenceEqual(bytes) && File.GetLastWriteTimeUtc(entry.Path) == stamp) unchangedNoWrite++;
                }
            }
            catch (Exception ex)
            {
                loadErrors++;
                modeAFailures.Add($"{id}: {ex.GetType().Name}");
            }
            if (total % 200 == 0) Console.WriteLine($"... {total}/{entries.Count} ({watch.Elapsed:mm\\:ss})");
        }

        // V4: a server-dialect Temp snapshot (INSPECT's load-time copy) against the next local-dialect snapshot of the
        // same inspection (INSPECT's save), ignoring the three *_Secure values and trailing event 40s.
        int v4Pairs = 0, v4Equal = 0;
        var v4Diffs = new Dictionary<string, int>();
        foreach (var server in serverTempFiles)
        {
            var match = Regex.Match(Path.GetFileNameWithoutExtension(server.Path), @"^(?<num>.+)_v(?<ver>\d+)$");
            if (!match.Success) continue;
            string dir = Path.GetDirectoryName(server.Path)!;
            int version = int.Parse(match.Groups["ver"].Value);
            var next = localTempFiles.Keys
                .Where(p => string.Equals(Path.GetDirectoryName(p), dir, StringComparison.OrdinalIgnoreCase))
                .Select(p => (Path: p, M: Regex.Match(Path.GetFileNameWithoutExtension(p), @"^(?<num>.+)_v(?<ver>\d+)$")))
                .Where(x => x.M.Success && x.M.Groups["num"].Value == match.Groups["num"].Value && int.Parse(x.M.Groups["ver"].Value) > version)
                .OrderBy(x => int.Parse(x.M.Groups["ver"].Value)).Select(x => x.Path).FirstOrDefault();
            if (next == null) continue;
            v4Pairs++;
            var saver = new SurgicalSaveService(new FailedSaveRecoveryService(Path.Combine(work, "Recovery")), null, Path.Combine(work, "VerifiedSaves"));
            var model = saver.Load(server.Path);
            var projected = Json.Parse(InspectProjectionWriter.Project(Json.Parse(Json.Utf8.GetBytes(saver.RenderPreservingSave(model)))).ToUtf8Bytes());
            var inspect = Json.Parse(localTempFiles[next]);
            foreach (var key in new[] { "InspectionFee_Secure", "InspectorOtherFee1_Secure", "InspectorOtherFee2_Secure" })
            {
                projected[key] = null; inspect[key] = null;
            }
            var inspectEvents = (JArray)inspect["Events"]!;
            var projectedEvents = (JArray)projected["Events"]!;
            while (inspectEvents.Count > projectedEvents.Count && (int?)inspectEvents.Last!["EventTypeId"] == 40) inspectEvents.Last!.Remove();
            var diff = Json.Diff(projected, inspect).Select(Json.Normalize).Distinct().ToList();
            if (diff.Count == 0) v4Equal++;
            foreach (var path in diff) v4Diffs[path] = v4Diffs.GetValueOrDefault(path) + 1;
        }

        report.AppendLine("# F-002 corpus run (sandbox copy)");
        report.AppendLine();
        report.AppendLine($"Files: {total} ({string.Join(", ", entries.GroupBy(e => e.Folder).Select(g => $"{g.Key} {g.Count()}"))}); load errors: {loadErrors}; time {watch.Elapsed:mm\\:ss}.");
        report.AppendLine();
        report.AppendLine($"- A-002-02 forced Mode A save byte-identical: {identical}/{total - loadErrors}");
        report.AppendLine($"- A-002-01 no-edit save wrote nothing (identical files): {unchangedNoWrite}/{identical}");
        report.AppendLine($"- F-004 V1: files whose edited-save render differs only by NA→NI Values: {naRewriteFiles}");
        report.AppendLine($"- A-002-10 / V3 INSPECT-local Temp snapshots reproduced byte for byte: {localTempIdentical}/{localTemp}");
        report.AppendLine($"- V4 server Temp snapshot -> next local snapshot equal (ignoring *_Secure, trailing 40): {v4Equal}/{v4Pairs}");
        report.AppendLine($"- V5 projected top-level keys equal SPEC B1: {keyOrderOk}/{total - loadErrors}; dropped keys absent: {droppedOk}/{total - loadErrors}");
        report.AppendLine($"- Q-21 fee fields byte for byte in Mode B: {feesOk}/{total - loadErrors}");
        report.AppendLine($"- Projection warnings: {warnings}");
        if (modeADiffs.Count > 0)
        {
            report.AppendLine(); report.AppendLine("## Mode A differences by JSON path (file count)");
            foreach (var kv in modeADiffs.OrderByDescending(k => k.Value)) report.AppendLine($"- `{kv.Key}`: {kv.Value}");
            report.AppendLine(); report.AppendLine("## Mode A differing files");
            foreach (var line in modeAFailures.Take(60)) report.AppendLine("- " + line);
        }
        if (v3Failures.Count > 0)
        {
            report.AppendLine(); report.AppendLine("## V3 differing snapshots");
            foreach (var line in v3Failures) report.AppendLine("- " + line);
        }
        if (v4Diffs.Count > 0)
        {
            report.AppendLine(); report.AppendLine("## V4 differences by JSON path (pair count)");
            foreach (var kv in v4Diffs.OrderByDescending(k => k.Value)) report.AppendLine($"- `{kv.Key}`: {kv.Value}");
        }
        string text = report.ToString();
        Console.WriteLine(text);
        if (reportPath != null) File.WriteAllText(reportPath, text, Json.Utf8);
        bool ok = loadErrors == 0 && identical == total && localTempIdentical == localTemp && keyOrderOk == total && droppedOk == total && feesOk == total;
        return ok ? 0 : 1;
    }
}
