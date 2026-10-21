// F-018 Review harness (program/features/F-018-review-approve/ACCEPTANCE.md). Replaces ApproveHarness: the owner
// withdrew Approve on 2026-10-20 ("I really don't approve anything"). Review is the owner's holding folder for their own
// automation, so RED shows and opens Review files but never submits, moves or rewrites one on its own.
//   dotnet run -p:Platform=x64
// Sandbox Inspections folders under %TEMP% only; synthetic reports (job 7654321, initials AB, 100 Example St). No network.
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using InspectionEditor.Models;
using InspectionEditor.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

return await Harness.Run();

static class Harness
{
    private static int _passed, _failed;

    private static void Check(string id, bool ok, string what)
    {
        if (ok) { _passed++; Console.WriteLine($"PASS {id} {what}"); }
        else { _failed++; Console.WriteLine($"FAIL {id} {what}"); }
    }

    private const string Owner = "owner@example.com";
    private const string Inspector = "inspector@example.com";
    private const string Number = "7654321-ME-1-AB";

    private static CompleteProfile Profile(string folder) => new(folder, InspectUserKind.Inspector, "Rae V Example", Owner, "000-000-0000", "TX");

    private static JObject Item(int id, string number, string name, string control, object? value, bool required = false) => new()
    {
        ["ItemId"] = id, ["Name"] = name, ["Number"] = number, ["ControlName"] = control, ["ControlFormatString"] = null,
        ["ItemGroupId"] = "", ["ItemGroupControlName"] = null, ["PictureSizeId"] = 0, ["IsCopied"] = false, ["Hide"] = false,
        ["DisplayLabel"] = number + " " + name, ["MaxIncidents"] = 0, ["CommentList"] = new JArray(), ["ValueList"] = new JArray(),
        ["SortOrder"] = 0, ["Value"] = value == null ? JValue.CreateNull() : JToken.FromObject(value), ["ResultSortOrder"] = 0,
        ["Comments"] = "", ["Incidents"] = null, ["Pictures"] = new JArray(),
        ["Required"] = required, ["AlwaysPrint"] = false, ["OnlyPrint"] = "", ["Template"] = null, ["ButtonText"] = null,
        ["HideCommentsButton"] = false, ["HidePicturesButton"] = false, ["HideAddButton"] = false, ["IsPictureRequired"] = false
    };

    // A processed, valid Review file (IsApproval false, the inspector's SubmittorUserName, Version 1, LastDBDate).
    private static JObject Fixture(string number = Number) => new()
    {
        ["InspectVersion"] = null, ["WorkOrderId"] = "00000000-0000-0000-0000-000000000018", ["ServiceId"] = 1, ["FormId"] = 40,
        ["IsTransfer"] = false, ["IsPreinspection"] = false, ["IsApproval"] = false, ["NoCharge"] = false, ["NoChargeReason"] = null,
        ["NextActionId"] = 0, ["NextActionText"] = " ", ["InspectionName"] = "BMEP Rough", ["InspectionNumber"] = number,
        ["InspectionCode"] = "ME", ["Attempt"] = 1, ["StatusId"] = 2,
        ["StatusList"] = new JArray(new JObject { ["Id"] = 0, ["Name"] = "Ordered" }, new JObject { ["Id"] = 2, ["Name"] = "Passed" }),
        ["PlanNumber"] = "P1", ["BuilderName"] = "Example Builder", ["BuilderAccountId"] = "00000000-0000-0000-0000-000000000002",
        ["Contact"] = "Example Contact", ["ContactNumber"] = "000-000-0000", ["JobNumber"] = number[..7], ["Project"] = "Example Project",
        ["Address"] = "100 Example St", ["Lot"] = "1", ["Block"] = "A", ["City"] = "Example City", ["State"] = "TX",
        ["InspectorUsername"] = Inspector, ["InspectorName"] = "Ann B Example", ["InspectorPhone"] = "000-000-0000",
        ["SubmittorUserName"] = Inspector, ["DateInspected"] = "2026-10-16T10:00:00-05:00", ["TimePeriodInspected"] = "",
        ["StructureTypeId"] = 0, ["CreatedDate"] = "2026-10-01T08:00:00-05:00", ["LastModifiedDate"] = "2026-10-16T11:00:00-05:00",
        ["LastDBDate"] = "2026-10-16T12:00:00-05:00", ["Version"] = 1, ["ErrorMessage"] = null, ["LastModifiedUsername"] = null,
        ["IsReinspection"] = false, ["PurchaseOrderRequired"] = false, ["PurchaseOrderNumber"] = "", ["RequisitionNumber"] = null,
        ["SuperintendentName"] = "Super A", ["Subcontractor"] = null, ["ManuallyEnteredAddress"] = false, ["Requests"] = null,
        ["WhoNotified"] = null, ["WhenNotified"] = null, ["Signature"] = "/9j/AA==", ["DocumentTypeId"] = "00000000-0000-0000-0000-000000000003",
        ["InspectionFee_Secure"] = "PLACEHOLDERCIPHERTEXTA0==", ["InspectorOtherFee1_Secure"] = null, ["InspectorOtherFee2_Secure"] = null,
        ["OtherFeeDescription1"] = null, ["OtherFeeDescription2"] = null, ["InternalNotes"] = null,
        ["Sections"] = new JArray(
            new JObject { ["SectionId"] = 1, ["Name"] = "Administrative", ["ControlName"] = "Simple", ["Number"] = "1",
                ["Items"] = new JArray(Item(101, "1.1", "Superintendent", "SearchLookup", "Super A")) },
            new JObject { ["SectionId"] = 5, ["Name"] = "Rough", ["ControlName"] = "SectionStandardControl", ["Number"] = "5",
                ["Items"] = new JArray(Item(501, "5.1", "Plumbing", "PassFailNaNi", "Pass", required: true)) }),
        ["Events"] = new JArray(
            new JObject { ["Date"] = "2026-10-01T08:00:00-05:00", ["EventTypeId"] = 30, ["Username"] = Inspector, ["UserDisplayName"] = "Ann B Example", ["AdditionalInfo"] = null }),
        ["Attachments"] = new JArray()
    };

    private static JObject Read(string path)
    {
        using var reader = new JsonTextReader(new StringReader(File.ReadAllText(path))) { DateParseHandling = DateParseHandling.None };
        return JObject.Load(reader);
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private sealed class Sandbox : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "red-f018-" + Guid.NewGuid().ToString("N"));
        public string IF => Path.Combine(Root, "Inspections");
        public string Review => Path.Combine(IF, "Review");
        public string MyList => Path.Combine(IF, "MyList");
        public string Outbox => Path.Combine(IF, "Outbox");
        public string Archive => Path.Combine(IF, "Archive");
        public string Local => Path.Combine(Root, "RedLocal");
        public Sandbox()
        {
            foreach (var d in new[] { Review, MyList, Outbox, Archive, Local }) Directory.CreateDirectory(d);
        }
        public string Place(JObject doc, string? name = null, string? folder = null)
        {
            string path = Path.Combine(folder ?? Review, (name ?? (string)doc["InspectionNumber"]!) + ".ins");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, doc.ToString(Formatting.None), new UTF8Encoding(false));
            return path;
        }
        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }

    private sealed class ListLog : ISubmitLog
    {
        public List<string> Lines { get; } = new();
        public void Inspect(string line) => Lines.Add(line);
        public void Red(string line) => Lines.Add("R " + line);
    }

    private static (SubmitContext Ctx, ListLog Log) Context(Sandbox box, string path)
    {
        var saver = new SurgicalSaveService();
        var model = saver.Load(path);
        var log = new ListLog();
        var ctx = new SubmitContext
        {
            Model = model, Saver = saver, SourcePath = path, Profile = Profile(box.IF), RawUserType = "Inspector",
            SaveWithSweeps = () => { saver.Save(model); return true; }, LocalRoot = box.Local, Log = log, Sleep = _ => { }
        };
        return (ctx, log);
    }

    public static async Task<int> Run()
    {
        // ---------- A-018-02 / A-018-08 / W1 / W3 Who sees Review (owner 2026-10-20: not a setting) ----------
        using (var box = new Sandbox())
        {
            Check("A-018-02", !ReviewAccess.Allowed(box.IF), "empty Review folder: no Review view (common inspectors never see it)");
            box.Place(Fixture());
            Check("A-018-02", ReviewAccess.Allowed(box.IF) && ReviewAccess.CountReviewFiles(box.IF) == 1, "one .ins in Review: the Review view appears (count without parsing)");
            Check("A-018-08", !ReviewAccess.Allowed(Path.Combine(box.Root, "none")) &&
                              ReviewAccess.MoveRefused == "You have insufficient permissions." &&
                              ReviewAccess.ViewRefused == "You have insufficient permissions to view the Review folder.",
                "no Review reports: the Review view and moves to Review are refused with INSPECT's messages");
            Check("W1", typeof(ReviewAccess).Assembly.GetType("InspectionEditor.Services.ReviewSettings") == null &&
                        !File.ReadAllText(Path.Combine("..", "..", "InspectionPickerWindow.Housekeeping.cs")).Contains("\"Show Review"),
                "there is no Show Review setting (owner 2026-10-20)");
            Check("W3", ReviewAccess.IsInReview(Path.Combine(box.Review, "a.ins"), box.IF) &&
                        !ReviewAccess.IsInReview(Path.Combine(box.IF, "Review Notes", "a.ins"), box.IF) &&
                        !ReviewAccess.IsInReview(Path.Combine(box.MyList, "Review.ins"), box.IF),
                "a Review file is one whose parent folder is exactly {IF}\\Review (not any path containing Review)");
        }

        // ---------- A-018-21 No Approve anywhere ----------
        {
            var red = typeof(SubmitService).Assembly;
            var main = red.GetType("InspectionEditor.MainWindow");
            var picker = red.GetType("InspectionEditor.InspectionPickerWindow");
            const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            Check("A-018-21", typeof(SubmitContext).GetProperty("Approve") == null &&
                              typeof(ReviewAccess).GetMethods(all).All(m => !m.Name.Contains("Approve", StringComparison.Ordinal)) &&
                              typeof(SubmitService).GetFields(all).All(f => !f.Name.StartsWith("Approve", StringComparison.Ordinal)) &&
                              main != null && main.GetMethods(all).All(m => !m.Name.Contains("Approve", StringComparison.Ordinal)) &&
                              picker != null && picker.GetMethods(all).All(m => !m.Name.Contains("Approve", StringComparison.Ordinal)) &&
                              red.GetType("InspectionEditor.Services.ApproveRunResult") == null,
                "no Approve mode in Submit, no list Approve, no editor Approve, no Approve button code");
        }

        // ---------- A-018-22 Submit never takes a Review file ----------
        using (var box = new Sandbox())
        {
            string src = box.Place(Fixture());
            string before = Hash(src);
            var (ctx, log) = Context(box, src);
            var result = await SubmitService.SubmitAsync(ctx);
            Check("A-018-22", result.Stage == SubmitStage.Refused && result.Message == "Review files are not submitted from RED." &&
                              Hash(src) == before && Directory.GetFiles(box.Outbox).Length == 0 && Directory.GetFiles(box.Archive).Length == 0 &&
                              log.Lines.Count == 0 && (string?)Read(src)["SubmittorUserName"] == Inspector,
                $"refused before anything is logged, stamped or saved; the Review bytes are unchanged ({result.Stage})");
            // Any folder named Review (a practice copy outside the profile's Inspections folder) is refused as well.
            string practice = box.Place(Fixture(), folder: Path.Combine(box.Root, "Elsewhere", "Review"));
            string practiceHash = Hash(practice);
            var (pctx, _) = Context(box, practice);
            var presult = await SubmitService.SubmitAsync(pctx);
            Check("A-018-22", presult.Stage == SubmitStage.Refused && Hash(practice) == practiceHash &&
                              !Directory.Exists(Path.Combine(box.Root, "Elsewhere", "Outbox")),
                "a practice Review folder is refused too; no Outbox is created next to it");
        }

        // ---------- A-018-23 The editor shows no Submit for a Review file ----------
        using (var box = new Sandbox())
        {
            string review = Path.Combine(box.Review, Number + ".ins");
            var state = SubmitAvailability.For(reportOpen: true, hasFile: true, review, running: false);
            var myList = SubmitAvailability.For(true, true, Path.Combine(box.MyList, Number + ".ins"), false);
            Check("A-018-23", !state.Visible && !state.Enabled && state.Hint == "" && myList.Visible && myList.Enabled,
                "a Review file: no Submit button and no hint (no Approve either); a My List file: Submit");
        }

        // ---------- A-018-24 A pending delete left by an older RED's Approve never deletes a Review file ----------
        using (var box = new Sandbox())
        {
            string src = box.Place(Fixture());
            byte[] bytes = File.ReadAllBytes(src);
            bool removed;
            using (new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.Read)) // held: the delete cannot happen
                removed = SubmitService.RemoveSource(src, bytes, box.Local, _ => { });
            bool queued = SubmitService.PendingPaths(box.Local).Any(p => string.Equals(p, src, StringComparison.OrdinalIgnoreCase));
            var messages = SubmitService.ProcessPendingDeletes(box.Local);
            Check("A-018-24", !removed && queued && File.Exists(src) && File.ReadAllBytes(src).AsSpan().SequenceEqual(bytes) &&
                              messages.Count == 0 && SubmitService.PendingPaths(box.Local).Count == 0,
                "the launch-time pending delete drops the Review entry and keeps the file byte for byte");
        }

        // ---------- A-018-25 A Review file changed elsewhere is never saved over (F-002 E1) ----------
        using (var box = new Sandbox())
        {
            string src = box.Place(Fixture());
            var saver = new SurgicalSaveService();
            var model = saver.Load(src);
            model.Sections[1].Items[0].Comments = "edited in RED";
            var theirs = Read(src);
            theirs["Requests"] = "changed by the owner's automation on another computer";
            File.WriteAllText(src, theirs.ToString(Formatting.None), new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(src, DateTime.UtcNow.AddSeconds(5));
            string otherHash = Hash(src);
            bool refused = false;
            try { saver.Save(model); }
            catch (InspectionChangedOnDiskException) { refused = true; }
            Check("A-018-25", refused && Hash(src) == otherHash, "RED's save stops; the file keeps the other computer's bytes");
        }

        // ---------- A-018-07 Move to Review (FolderMoveService, unchanged) ----------
        using (var box = new Sandbox())
        {
            string archived = box.Place(Fixture(), folder: box.Archive);
            byte[] bytes = File.ReadAllBytes(archived);
            var moved = FolderMoveService.Move(archived, box.IF, InspectFolder.Review, _ => true, box.Local, _ => { });
            string target = Path.Combine(box.Review, Number + ".ins");
            Check("A-018-07", moved.Outcome == MoveOutcome.Moved && !File.Exists(archived) && File.ReadAllBytes(target).AsSpan().SequenceEqual(bytes),
                "an Archive file is byte-moved to Review");
        }

        Console.WriteLine();
        Console.WriteLine($"F-018 harness: {_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }
}
