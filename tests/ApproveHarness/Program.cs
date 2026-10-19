// F-018 Review and Approve harness (program/features/F-018-review-approve/ACCEPTANCE.md).
//   dotnet run -p:Platform=x64
// Sandbox Inspections folders under %TEMP% only; synthetic reports (job 7654321, initials AB, 100 Example St). No network:
// Approve never uploads. A-018-01 (live Review count) is read-only on live data and is not run here.
using System.IO;
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

    private const string Reviewer = "reviewer@example.com";
    private const string Inspector = "inspector@example.com";
    private const string Number = "7654321-ME-1-AB";

    private static CompleteProfile Profile(string folder, InspectUserKind kind = InspectUserKind.Inspector) =>
        new(folder, kind, "Rae V Example", Reviewer, "000-000-0000", "TX");

    private static JObject Item(int id, string number, string name, string control, object? value, string? comments = "", bool required = false) => new()
    {
        ["ItemId"] = id, ["Name"] = name, ["Number"] = number, ["ControlName"] = control, ["ControlFormatString"] = null,
        ["ItemGroupId"] = "", ["ItemGroupControlName"] = null, ["PictureSizeId"] = 0, ["IsCopied"] = false, ["Hide"] = false,
        ["DisplayLabel"] = number + " " + name, ["MaxIncidents"] = 0, ["CommentList"] = new JArray(), ["ValueList"] = new JArray(),
        ["SortOrder"] = 0, ["Value"] = value == null ? JValue.CreateNull() : JToken.FromObject(value), ["ResultSortOrder"] = 0,
        ["Comments"] = comments, ["Incidents"] = null, ["Pictures"] = new JArray(),
        ["Required"] = required, ["AlwaysPrint"] = false, ["OnlyPrint"] = "", ["Template"] = null, ["ButtonText"] = null,
        ["HideCommentsButton"] = false, ["HidePicturesButton"] = false, ["HideAddButton"] = false, ["IsPictureRequired"] = false
    };

    // A processed Review file: IsApproval false, IsTransfer false, the inspector's SubmittorUserName, Version 1, LastDBDate.
    private static JObject Fixture(string number = Number, bool valid = true) => new()
    {
        ["InspectVersion"] = null, ["WorkOrderId"] = "00000000-0000-0000-0000-000000000018", ["ServiceId"] = 1, ["FormId"] = 40,
        ["IsTransfer"] = false, ["IsPreinspection"] = false, ["IsApproval"] = false, ["NoCharge"] = false, ["NoChargeReason"] = null,
        ["NextActionId"] = 0, ["NextActionText"] = " ", ["InspectionName"] = "BMEP Rough", ["InspectionNumber"] = number,
        ["InspectionCode"] = "ME", ["Attempt"] = 1, ["StatusId"] = valid ? 2 : 0,
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
            new JObject { ["Date"] = "2026-10-01T08:00:00-05:00", ["EventTypeId"] = 30, ["Username"] = Inspector, ["UserDisplayName"] = "Ann B Example", ["AdditionalInfo"] = null },
            new JObject { ["Date"] = "2026-10-16T12:00:00-05:00", ["EventTypeId"] = 31, ["Username"] = Inspector, ["UserDisplayName"] = "Ann B Example", ["AdditionalInfo"] = "Submitting inspection" }),
        ["Attachments"] = new JArray()
    };

    private static JObject Read(string path)
    {
        using var reader = new JsonTextReader(new StringReader(File.ReadAllText(path))) { DateParseHandling = DateParseHandling.None };
        return JObject.Load(reader);
    }

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

    private static (SubmitContext Ctx, SurgicalSaveService Saver, InspectionFile Model, ListLog Log) Context(Sandbox box, string path, bool approve,
        InspectUserKind kind = InspectUserKind.Inspector, bool openAttachment = false, Action<InspectionFile>? edit = null)
    {
        var saver = new SurgicalSaveService();
        var model = saver.Load(path);
        edit?.Invoke(model);
        var log = new ListLog();
        var ctx = new SubmitContext
        {
            Approve = approve, Model = model, Saver = saver, SourcePath = path, Profile = Profile(box.IF, kind), RawUserType = kind.ToString(),
            SaveWithSweeps = () => { saver.Save(model); return true; }, HasOpenAttachment = () => openAttachment,
            LocalRoot = box.Local, Log = log, Sleep = _ => { }
        };
        return (ctx, saver, model, log);
    }

    public static async Task<int> Run()
    {
        // ---------- A-018-02 / A-018-08 / W3 Who sees Review ----------
        using (var box = new Sandbox())
        {
            string settings = Path.Combine(box.Root, "settings");
            var auto = new ReviewSettings { ShowReview = ReviewVisibility.Auto };
            Check("A-018-02", !ReviewAccess.Allowed(box.IF, auto), "auto, empty Review: no Review view");
            box.Place(Fixture());
            Check("A-018-02", ReviewAccess.Allowed(box.IF, auto) && ReviewAccess.CountReviewFiles(box.IF) == 1, "auto, one .ins: the Review view appears (count without parsing)");
            Check("A-018-08", !ReviewAccess.Allowed(box.IF, new ReviewSettings { ShowReview = ReviewVisibility.Never }) &&
                              ReviewAccess.MoveRefused == "You have insufficient permissions." &&
                              ReviewAccess.ApprovePermissionRefused == "You do not have permissions to Approve inspections.",
                "never: Review, Move to Review and Approve are refused with INSPECT's messages");
            Check("W1", ReviewAccess.Allowed(Path.Combine(box.Root, "none"), new ReviewSettings { ShowReview = ReviewVisibility.Always }), "always: shown even without files");
            Check("W3", ReviewAccess.IsInReview(Path.Combine(box.Review, "a.ins"), box.IF) &&
                        !ReviewAccess.IsInReview(Path.Combine(box.IF, "Review Notes", "a.ins"), box.IF) &&
                        !ReviewAccess.IsInReview(Path.Combine(box.MyList, "Review.ins"), box.IF),
                "Approve only for a file whose parent folder is exactly {IF}\\Review (not any path containing Review)");
            new ReviewSettings { ShowReview = ReviewVisibility.Never }.Save(settings);
            Check("W1", ReviewSettings.Load(settings).ShowReview == ReviewVisibility.Never, "the setting is kept in RED's app data");
        }

        // ---------- A-018-04 / A-018-10 / A-018-11 Approve places the file; exact bytes; events ----------
        using (var approveBox = new Sandbox())
        using (var submitBox = new Sandbox())
        {
            string src = approveBox.Place(Fixture());
            byte[] source = File.ReadAllBytes(src);
            var (ctx, _, _, log) = Context(approveBox, src, approve: true);
            var result = await SubmitService.SubmitAsync(ctx);
            string archive = Path.Combine(approveBox.Archive, Number + ".ins"), outbox = Path.Combine(approveBox.Outbox, Number + ".ins");
            var sent = File.Exists(outbox) ? Read(outbox) : new JObject();
            Check("A-018-04", result.Stage == SubmitStage.Done && File.Exists(archive) && File.Exists(outbox) &&
                              File.ReadAllBytes(archive).AsSpan().SequenceEqual(File.ReadAllBytes(outbox)) && !File.Exists(src) &&
                              !File.Exists(Path.Combine(approveBox.MyList, Number + ".ins")) &&
                              (string?)sent["SubmittorUserName"] == Reviewer && (bool?)sent["IsApproval"] == true,
                $"Archive and Outbox identical, Review copy gone, no My List copy, SubmittorUserName reviewer, IsApproval true ({result.Stage}: {result.Message})");
            Check("A1", result.Message == "Approved and sent to Outbox. Strand usually picks it up within a few minutes." &&
                        log.Lines.First() == $"Approving Inspection - {Number}" &&
                        log.Lines.Contains($"Moving Inspection to Archive - {Number}") && log.Lines.Contains($"Moving Inspection to Outbox - {Number}"),
                "A1 step 8 message; INSPECT's Approving / Moving lines");
            var names = sent.Properties().Select(p => p.Name).ToList();
            Check("A-018-10", names.Count == 63 && names.IndexOf("IsApproval") == 7 && names[6] == "NoCharge" && names[8] == "InspectionName" &&
                              !names.Contains("ElectricProvider") && !names.Contains("Rejected") && !names.Contains("Filename"),
                "INSPECT-local dialect: 63 keys, IsApproval the 8th between NoCharge and InspectionName");
            var original = JObject.Load(new JsonTextReader(new StringReader(Encoding.UTF8.GetString(source))) { DateParseHandling = DateParseHandling.None });
            Check("A-018-10", new[] { "Version", "LastDBDate", "InspectorUsername", "InspectorName", "Signature", "DateInspected", "StatusId", "InspectionFee_Secure", "InspectorOtherFee1_Secure", "InspectorOtherFee2_Secure" }
                                .All(k => JToken.DeepEquals(original[k], sent[k])),
                "Version, LastDBDate, InspectorUsername, InspectorName, Signature, DateInspected, StatusId and the *_Secure strings unchanged");
            Check("A-018-11", JToken.DeepEquals(original["Events"], sent["Events"]), "Events identical to the source (no 36, 31 or 40 appended)");

            // The same source submitted plainly by the same profile.
            string plain = submitBox.Place(Fixture(), folder: submitBox.MyList);
            var (sctx, _, _, _) = Context(submitBox, plain, approve: false);
            var sresult = await SubmitService.SubmitAsync(sctx);
            string sOut = Path.Combine(submitBox.Outbox, Number + ".ins");
            string approvedText = File.ReadAllText(outbox).Replace("\"IsApproval\":true", "\"IsApproval\":false");
            Check("A-018-10", sresult.Stage == SubmitStage.Done && approvedText == File.ReadAllText(sOut),
                "the Outbox bytes equal a plain submit's except \"IsApproval\":true");
        }

        // ---------- A-018-09 Review source with a different file name ----------
        using (var box = new Sandbox())
        {
            string src = box.Place(Fixture(), name: Number + " (copy)");
            var (ctx, _, _, _) = Context(box, src, approve: true);
            var result = await SubmitService.SubmitAsync(ctx);
            Check("A-018-09", result.Stage == SubmitStage.Done && File.Exists(Path.Combine(box.Archive, Number + ".ins")) &&
                              File.Exists(Path.Combine(box.Outbox, Number + ".ins")) && !File.Exists(src),
                "Archive and Outbox hold {num}.ins and the (copy) Review file is gone");
        }

        // ---------- A-018-05 / A-018-12 Refused approve leaves no flag ----------
        using (var box = new Sandbox())
        {
            string src = box.Place(Fixture(valid: false));
            var (ctx, _, _, _) = Context(box, src, approve: true, edit: m => m.Sections[1].Items[0].Comments = "edited by reviewer");
            var result = await SubmitService.SubmitAsync(ctx);
            var after = Read(src);
            Check("A-018-05", result.Stage == SubmitStage.Invalid &&
                              result.Message == "Cannot approve inspection. You need to fix one or more problems with the inspection." &&
                              Directory.GetFiles(box.Outbox).Length == 0 && Directory.GetFiles(box.Archive).Length == 0,
                "invalid: INSPECT's Approve message and nothing moves");
            Check("A-018-12", (string?)after["Sections"]![1]!["Items"]![0]!["Comments"] == "edited by reviewer" && (bool?)after["IsApproval"] == false &&
                              (string?)after["SubmittorUserName"] == Inspector,
                "the Review file keeps the edit, IsApproval false and the inspector's SubmittorUserName");
        }

        // ---------- A-018-13 Open attachment stops approve before the flag ----------
        using (var box = new Sandbox())
        {
            string src = box.Place(Fixture());
            var (ctx, saver, _, _) = Context(box, src, approve: true, openAttachment: true);
            var result = await SubmitService.SubmitAsync(ctx);
            Check("A-018-13", result.Stage == SubmitStage.OpenAttachment && result.Message == "You have an open attachment. Please save and close it before continuing." &&
                              (bool?)Read(src)["IsApproval"] == false && (bool?)saver.GetHeaderValue("IsApproval") == false && Directory.GetFiles(box.Outbox).Length == 0,
                "the open attachment stops it; no flag in the file or in memory; nothing moved");
        }

        // ---------- A-018-14 Submit clean-ups run during approve ----------
        using (var box = new Sandbox())
        {
            var doc = Fixture();
            doc["IsTransfer"] = true;
            var items = (JArray)doc["Sections"]![1]!["Items"]!;
            items.Add(Item(501, "5.1", "Plumbing", "PassFailNaNi", "Pass")); // blank duplicate Pass row
            ((JArray)doc["Attachments"]!).Add(new JObject
            {
                ["InspectVersion"] = null, ["Id"] = Guid.NewGuid().ToString(), ["EditPath"] = null, ["FileData"] = Convert.ToBase64String(Encoding.ASCII.GetBytes("%PDF-1.4\n%%EOF")),
                ["FileType"] = "pdf", ["Filename"] = "7654321.pdf", ["AnnotatedPages"] = new JArray(), ["IncludedPages"] = new JArray(),
                ["ReferenceOnly"] = true, ["LastWriteTime"] = "2026-10-16T09:00:00-05:00", ["CanExcludePages"] = true
            });
            string src = box.Place(doc);
            var (ctx, _, _, _) = Context(box, src, approve: true);
            var result = await SubmitService.SubmitAsync(ctx);
            var sent = Read(Path.Combine(box.Outbox, Number + ".ins"));
            Check("A-018-14", result.Stage == SubmitStage.Done && (bool?)sent["IsTransfer"] == false && ((JArray)sent["Sections"]![1]!["Items"]!).Count == 1 &&
                              ((JArray)sent["Attachments"]!).Count == 0 && (bool?)sent["IsApproval"] == true,
                "IsTransfer false, the blank duplicate row removed, no reference-only attachment, IsApproval true");
        }

        // ---------- A-018-15 Archive copy fails ----------
        using (var box = new Sandbox())
        {
            string src = box.Place(Fixture());
            var original = Read(src);
            string archive = Path.Combine(box.Archive, Number + ".ins");
            File.WriteAllText(archive, "{}");
            SubmitResult failed;
            using (new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                var (ctx, _, _, _) = Context(box, src, approve: true);
                failed = await SubmitService.SubmitAsync(ctx);
            }
            var held = Read(src);
            Check("A-018-15", failed.Stage == SubmitStage.ArchiveFailed && failed.Message.StartsWith("Copy to Archive folder failed. ") &&
                              !failed.Message.Contains("Cannot submit inspection.") && (bool?)held["IsApproval"] == true &&
                              (string?)held["SubmittorUserName"] == Reviewer && !File.Exists(Path.Combine(box.Outbox, Number + ".ins")),
                "\"Copy to Archive folder failed. {reason}\"; the Review file holds the flag and the reviewer; no Outbox file");
            var (retry, _, _, _) = Context(box, src, approve: true);
            var ok = await SubmitService.SubmitAsync(retry);
            Check("A-018-15", ok.Stage == SubmitStage.Done && JToken.DeepEquals(Read(Path.Combine(box.Outbox, Number + ".ins"))["Events"], original["Events"]),
                "after the lock is released Approve succeeds and the Events still equal the original source's");
        }

        // ---------- A-018-16 Every user type goes to the Outbox ----------
        foreach (var kind in new[] { InspectUserKind.Preinspector, InspectUserKind.NonDropBox })
        {
            using var box = new Sandbox();
            string src = box.Place(Fixture());
            var (ctx, _, _, _) = Context(box, src, approve: true, kind: kind);
            var result = await SubmitService.SubmitAsync(ctx);
            Check("A-018-16", result.Stage == SubmitStage.Done && File.Exists(Path.Combine(box.Outbox, Number + ".ins")) &&
                              result.Message == (kind == InspectUserKind.NonDropBox
                                  ? "Approved and sent to Outbox. Press Sync in the Outbox to send it to Strand."
                                  : "Approved and sent to Outbox. Strand usually picks it up within a few minutes."),
                $"{kind}: placed in the Outbox with no website upload (ctx.Website is null){(kind == InspectUserKind.NonDropBox ? ", with the Sync note" : "")}");
        }

        // ---------- A-018-06 Double tap ----------
        {
            string path = Path.Combine(Path.GetTempPath(), "red-f018-tap.ins");
            bool first = ReviewAccess.TryBegin(path), second = ReviewAccess.TryBegin(path);
            ReviewAccess.End(path);
            Check("A-018-06", first && !second && ReviewAccess.TryBegin(path), "a second Approve of the same file is refused while the first runs");
            ReviewAccess.End(path);
        }

        // ---------- A-018-17 / A-018-18 List approve ----------
        using (var box = new Sandbox())
        {
            string a = box.Place(Fixture("7654321-ME-1-AA"));
            string b = box.Place(Fixture("7654321-ME-1-BB", valid: false));
            string c = box.Place(Fixture("7654321-ME-1-CC"));
            var run = await ReviewAccess.ApproveFilesAsync(new[] { a, b, c }, Profile(box.IF), "Inspector", _ => false, new ListLog(), box.Local, _ => { });
            Check("A-018-17", run.Approved == 1 && File.Exists(Path.Combine(box.Outbox, "7654321-ME-1-AA.ins")) && File.Exists(b) && File.Exists(c) &&
                              (bool?)Read(b)["IsApproval"] == false && (bool?)Read(c)["IsApproval"] == false &&
                              run.Summary.StartsWith("1 of 3 inspections were approved.") &&
                              run.Summary.Contains("Cannot approve inspection. You need to fix one or more problems with the inspection."),
                "A approved; B and C stay in Review without the flag; \"1 of 3 inspections were approved.\" with B's message");
            var open = await ReviewAccess.ApproveFilesAsync(new[] { c }, Profile(box.IF), "Inspector", p => p == c, new ListLog(), box.Local, _ => { });
            Check("A-018-18", open.Approved == 0 && File.Exists(c) && open.FailedMessage == "7654321-ME-1-CC is open. Approve it from its window, or close it first.",
                "a file open in an editor is refused with RED's message and nothing moves");
        }

        // ---------- A-018-19 External change blocks approve ----------
        using (var box = new Sandbox())
        {
            string src = box.Place(Fixture());
            var (ctx, _, _, _) = Context(box, src, approve: true);
            var changed = Read(src);
            changed["Requests"] = "changed by another program";
            File.WriteAllText(src, changed.ToString(Formatting.None), new UTF8Encoding(false));
            byte[] theirs = File.ReadAllBytes(src);
            var result = await SubmitService.SubmitAsync(ctx);
            Check("A-018-19", result.Stage == SubmitStage.ExternalChange && File.ReadAllBytes(src).AsSpan().SequenceEqual(theirs) &&
                              (bool?)Read(src)["IsApproval"] == false && Directory.GetFiles(box.Outbox).Length == 0,
                "the external-change stop: nothing moves and the file keeps the other program's bytes with IsApproval false");
        }

        // ---------- W3 list approve never takes a file outside {IF}\Review ----------
        using (var box = new Sandbox())
        {
            string mine = box.Place(Fixture(), folder: box.MyList);
            var run = await ReviewAccess.ApproveFilesAsync(new[] { mine }, Profile(box.IF), "Inspector", _ => false, new ListLog(), box.Local, _ => { });
            Check("W3", run.Approved == 0 && run.FailedMessage == ReviewAccess.ApprovePermissionRefused && (bool?)Read(mine)["IsApproval"] == false &&
                        Directory.GetFiles(box.Outbox).Length == 0,
                "a My List file handed to the list Approve is refused; no flag, nothing moved");
        }

        // ---------- A1 step 7: a failure before the final write takes the stamp back ----------
        using (var box = new Sandbox())
        {
            string src = box.Place(Fixture());
            var (ctx, saver, _, _) = Context(box, src, approve: true);
            File.SetAttributes(src, FileAttributes.ReadOnly); // the Mode A save cannot write
            var result = await SubmitService.SubmitAsync(ctx);
            File.SetAttributes(src, FileAttributes.Normal);
            Check("A1", result.Stage != SubmitStage.Done && (bool?)saver.GetHeaderValue("IsApproval") == false &&
                        (string?)saver.GetHeaderValue("SubmittorUserName") == Inspector && (bool?)Read(src)["IsApproval"] == false &&
                        Directory.GetFiles(box.Outbox).Length == 0 && Directory.GetFiles(box.Archive).Length == 0,
                $"a write failure after the stamp: the flag is taken back in memory and the file has none ({result.Stage})");
        }

        // ---------- A-018-07 Move to Review ----------
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
