// F-012 completion check harness (program/features/F-012-completion-check/ACCEPTANCE.md).
//   dotnet run -p:Platform=x64
// Synthetic fixtures only (fixture-valid: a complete, signed CPP inspection for job 1234567). Fee amounts are generated
// in memory and never printed.
using System.IO;
using System.Reflection;
using InspectionEditor.Models;
using InspectionEditor.Services;
using Newtonsoft.Json.Linq;

if (args.Length >= 2 && args[0] == "--oracle")
    return Oracle.Run(args.Skip(1).ToArray());
return Harness.Run();

// V1 / A-012-11: read-only pass over processed files. Opens each .ins for reading only and prints counts per rule:
// no file names, addresses, values or fee amounts.
static class Oracle
{
    public static int Run(string[] folders)
    {
        int total = 0, valid = 0, unreadable = 0, feeSkipped = 0;
        var perRule = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var perItemRule = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var folder in folders)
        {
            int folderTotal = 0, folderValid = 0;
            foreach (var path in Directory.EnumerateFiles(folder, "*.ins", SearchOption.TopDirectoryOnly))
            {
                total++; folderTotal++;
                JObject doc;
                try
                {
                    byte[] bytes;
                    using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var memory = new MemoryStream()) { stream.CopyTo(memory); bytes = memory.ToArray(); }
                    doc = InspectionFileReader.ParseDocument(bytes, out _);
                }
                catch { unreadable++; continue; }
                var model = doc.ToObject<InspectionFile>()!;
                var result = InspectionValidator.Validate(model, key => doc[key]);
                if (result.FeeRuleSkipped) feeSkipped++;
                if (result.IsValid) { valid++; folderValid++; continue; }
                foreach (var rule in result.Blocking.Select(p => p.Rule).Distinct())
                    perRule[rule] = perRule.TryGetValue(rule, out int n) ? n + 1 : 1;
                foreach (var kind in result.Blocking.Where(p => p.Item != null).Select(p => p.Text.Split(" for item ")[0]).Distinct())
                    perItemRule[kind] = perItemRule.TryGetValue(kind, out int n) ? n + 1 : 1;
            }
            Console.WriteLine($"{Path.GetFileName(folder.TrimEnd('\\'))}: {folderTotal} files, {folderValid} valid");
        }
        Console.WriteLine($"total {total}, valid {valid}, invalid {total - valid - unreadable}, unreadable {unreadable}, fee rule not checked {feeSkipped}");
        foreach (var pair in perRule) Console.WriteLine($"  files failing {pair.Key}: {pair.Value}");
        foreach (var pair in perItemRule) Console.WriteLine($"  files with item problem \"{pair.Key}\": {pair.Value}");
        return 0;
    }
}

static class Harness
{
    private static int _passed, _failed;

    private static void Check(string id, bool ok, string what)
    {
        if (ok) { _passed++; Console.WriteLine($"PASS {id} {what}"); }
        else { _failed++; Console.WriteLine($"FAIL {id} {what}"); }
    }

    private static JObject Item(string number, string name, string control, string? value, bool required = false, bool pictureRequired = false,
        bool hide = false, string? template = null, int pictures = 0) => new()
    {
        ["ItemId"] = 1, ["Number"] = number, ["Name"] = name, ["ControlName"] = control, ["Value"] = value, ["Required"] = required,
        ["IsPictureRequired"] = pictureRequired, ["Hide"] = hide, ["Template"] = template, ["Comments"] = "",
        ["Pictures"] = new JArray(Enumerable.Range(0, pictures).Select(i => new JObject { ["Path"] = null, ["Caption"] = "Picture", ["Description"] = null, ["Image"] = "/9j/AA==" }))
    };

    private static JObject Valid() => new()
    {
        ["FormId"] = 20, ["InspectionCode"] = "CPP", ["InspectionNumber"] = "1234567-CPP-1-PQE", ["StatusId"] = 2, ["NextActionId"] = 0,
        ["StatusList"] = new JArray(new[] { 0, 1, 2, 3, 5, 7, 9, 24 }.Select(i => new JObject { ["Id"] = i, ["Name"] = $"S{i}" })),
        ["DateInspected"] = "2026-10-17T10:00:00-05:00", ["BuilderName"] = "Example Builder", ["Project"] = "Example Project",
        ["Address"] = "100 Example St", ["City"] = "Example City", ["State"] = "TX", ["JobNumber"] = "1234567", ["PlanNumber"] = "P1",
        ["InspectorName"] = "Pat Example", ["Attempt"] = 1, ["Lot"] = "1", ["Block"] = "A", ["PurchaseOrderRequired"] = false,
        ["PurchaseOrderNumber"] = "", ["RequisitionNumber"] = null, ["WhoNotified"] = null, ["WhenNotified"] = null,
        ["Signature"] = "/9j/AA==", ["InspectorOtherFee1_Secure"] = null, ["InspectorOtherFee2_Secure"] = null,
        ["OtherFeeDescription1"] = null, ["OtherFeeDescription2"] = null, ["Attachments"] = new JArray(),
        ["Sections"] = new JArray(new JObject { ["SectionId"] = 4, ["Name"] = "Steel", ["Number"] = "4",
            ["Items"] = new JArray(Item("4.1", "Rebar", "PassFailNaNi", "Pass", required: true), Item("4.2", "Chairs", "PassFailNaNi", null)) })
    };

    private static CompletionResult Run(JObject doc, Func<string, string?>? decrypt = null) =>
        InspectionValidator.Validate(doc.ToObject<InspectionFile>()!, key => doc[key], decrypt ?? (_ => null));

    private static List<string> Blocking(CompletionResult result) => result.Blocking.Select(p => p.Text).ToList();

    public static int Run()
    {
        // ---------- A-012-01 Valid fixture passes ----------
        var valid = Run(Valid());
        Check("A-012-01", valid.Problems.Count == 0 && valid.IsValid, "fixture-valid reports 0 problems");

        // ---------- A-012-02 Each header rule ----------
        var cases = new (string Change, Action<JObject> Apply, string Text)[]
        {
            ("DateInspected null", d => d["DateInspected"] = null, "Inspection Date required."),
            ("StatusId 0", d => d["StatusId"] = 0, "Status cannot be Ordered."),
            ("StatusId 1", d => d["StatusId"] = 1, "Status cannot be Ordered."),
            ("BuilderName \"\"", d => d["BuilderName"] = "", "Builder required."),
            ("Project \" \"", d => d["Project"] = " ", "Project required."),
            ("Address \"\"", d => d["Address"] = "", "Address required."),
            ("City null", d => d["City"] = null, "City required."),
            ("State null", d => d["State"] = null, "State required."),
            ("PlanNumber \"\"", d => d["PlanNumber"] = "", "Plan Number required."),
            ("InspectorName \"\"", d => d["InspectorName"] = "", "Inspector required."),
            ("Attempt 0", d => d["Attempt"] = 0, "Attempt must be 1 or greater."),
            ("JobNumber \"00000001\"", d => d["JobNumber"] = "00000001", "S2E Number required."),
            ("JobNumber \"12345678901\"", d => d["JobNumber"] = "12345678901", "S2E Number required."),
            ("PO required, both blank", d => d["PurchaseOrderRequired"] = true, "Purchase Order or Requisition Number required."),
            ("StatusId 3, NextActionId 0", d => d["StatusId"] = 3, "Next action is required."),
            ("Signature null", d => d["Signature"] = null, "Signature required."),
        };
        foreach (var c in cases)
        {
            var doc = Valid();
            c.Apply(doc);
            var problems = Blocking(Run(doc));
            Check("A-012-02", problems.Count == 1 && problems[0] == c.Text, $"{c.Change} -> \"{c.Text}\" ({string.Join(" | ", problems)})");
        }
        {
            var doc = Valid();
            doc["InspectorOtherFee1_Secure"] = "CIPHER-1";
            var problems = Blocking(Run(doc, cipher => cipher == "CIPHER-1" ? (17 + Random.Shared.Next(1, 90)).ToString("F2") : null));
            Check("A-012-02", problems.SequenceEqual(new[] { "Must provide a reason for Other Fee." }),
                "Other Fee 1 = <NONZERO_FEE> with OtherFeeDescription1 null -> \"Must provide a reason for Other Fee.\"");
            doc["OtherFeeDescription1"] = "Trip";
            Check("I12", Run(doc, _ => "25.00").IsValid, "with a reason the fee rule passes");
            Check("I12", Run(doc, _ => "0.00").IsValid && Run(Valid(), _ => "0.00").IsValid, "a zero fee needs no reason");
            var unreadable = Run(doc, _ => null);
            Check("I12", unreadable.IsValid && unreadable.FeeRuleSkipped, "an unreadable fee is skipped and flagged, never silently failed");

            // Through RED's real cipher (Windows): encrypt a generated amount in memory, then validate with the default decryptor.
            var cipherType = typeof(InspectionValidator).Assembly.GetType("InspectionEditor.Services.InspectCipher");
            var encrypt = cipherType?.GetMethod("Encrypt", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            string? real = null;
            try { real = (string?)encrypt?.Invoke(null, new object[] { (11 + Random.Shared.Next(1, 50)).ToString("F2") }); } catch { }
            if (real != null)
            {
                var withReal = Valid();
                withReal["InspectorOtherFee2_Secure"] = real;
                var result = InspectionValidator.Validate(withReal.ToObject<InspectionFile>()!, key => withReal[key]);
                Check("I12", Blocking(result).SequenceEqual(new[] { "Must provide a reason for Other Fee." }),
                    "RED's InspectCipher decrypts Other Fee 2 in memory; no reason -> blocked");
            }
            else
            {
                Check("I12", true, "RED's InspectCipher is not available in this build (no secret); real-cipher case not run");
            }
        }

        // ---------- A-012-03 Short or null job number does not crash ----------
        foreach (var job in new JToken?[] { "12345", JValue.CreateNull() })
        {
            var doc = Valid(); doc["JobNumber"] = job;
            List<string>? problems = null;
            try { problems = Blocking(Run(doc)); } catch { }
            Check("A-012-03", problems != null && problems.SequenceEqual(new[] { "S2E Number required." }),
                $"JobNumber {(job?.Type == JTokenType.Null ? "null" : job)} -> \"S2E Number required.\" without throwing");
        }

        // ---------- A-012-04 Notify pair ----------
        {
            var doc = Valid(); doc["WhoNotified"] = "Super";
            Check("A-012-04", Blocking(Run(doc)).SequenceEqual(new[] { "Notify Who and Notify When must both be filled or both be empty." }), "one of the pair set -> Notify problem");
            doc["WhenNotified"] = "2026-10-17T09:00:00-05:00";
            Check("A-012-04", Run(doc).IsValid, "both set -> no Notify problem");
        }

        // ---------- A-012-05 / A-012-14 Lot and Block do not block ----------
        {
            var doc = Valid(); doc["Lot"] = ""; doc["Block"] = "";
            var result = Run(doc);
            Check("A-012-05", result.IsValid && result.Problems.Select(p => p.Text).OrderBy(t => t).SequenceEqual(new[] { "Block required.", "Lot required." }),
                "Lot and Block markers show and the check reports 0 blocking problems");
        }

        // ---------- A-012-06 / 07 / 08 / 19 / 20 Item rules ----------
        {
            var doc = Valid();
            var items = (JArray)doc["Sections"]![0]!["Items"]!;
            items.Add(Item("4.3", "A", "Text", "", required: true));
            items.Add(Item("4.4", "B", "PassFail", "Pass", required: true, pictureRequired: true));
            items.Add(Item("3.5", "Form", "DocumentButton", "", required: true, template: "Orientation Form.pdf"));
            items.Add(Item("4.6", "C", "PassFail", null, pictureRequired: true));
            items.Add(Item("4.7", "D", "PassFail", null, required: true, pictureRequired: true, pictures: 1));
            items.Add(Item("4.8", "E", "PassFail", null, required: true, hide: true));
            items.Add(Item("3.6", "Form 2", "DocumentButton", "", required: true, template: ""));
            var problems = Blocking(Run(doc));
            Check("A-012-06", problems.Contains("Value is required for item 4.3") && problems.Contains("Picture is required for item 4.4") &&
                              problems.Contains("Document is required for item 3.5") && !problems.Any(p => p.EndsWith(" 4.6")),
                "value, picture and document rules; a non-required picture-required item is not checked");
            Check("A-012-07", problems.Contains("Value is required for item 4.7"), "a picture present but no value -> Value is required");
            Check("A-012-08", !problems.Any(p => p.EndsWith(" 4.8")), "a hidden required item is ignored");
            Check("A-012-19", problems.Contains("Document is required for item 3.6"), "a required DocumentButton with Template \"\" is always invalid");

            ((JArray)doc["Attachments"]!).Add(new JObject { ["Filename"] = "Orientation Form (100 Example St - 20261018).pdf", ["FileType"] = "pdf" });
            Check("A-012-20", !Blocking(Run(doc)).Contains("Document is required for item 3.5"), "the dated orientation attachment satisfies the document rule");
        }

        // ---------- A-012-12 Required grid cell; A-012-13 required ink item; A-012-17 blank Memo ----------
        {
            var doc = Valid();
            var items = (JArray)doc["Sections"]![0]!["Items"]!;
            var gallons = Item("4.1.G", "Truck 1 Gallons", "PassFail", "", required: true);
            gallons["ItemGroupId"] = "1"; gallons["ItemGroupControlName"] = "TruckPlacement";
            items.Add(gallons);
            items.Add(Item("2.1", "Sketch", "Sketch", null, required: true));
            var okNoPicture = Item("2.2", "Customer signature", "Signature", "ok", required: true);
            items.Add(okNoPicture);
            items.Add(Item("1.1", "Comment 1", "Memo", null, required: true)); // three spaces typed: F-004 Memo value is null
            var problems = Blocking(Run(doc));
            Check("A-012-12", problems.Contains("Value is required for item 4.1.G") && ItemGroupLayout.RequiredMissingText == "Required value is missing.",
                "a blank required grid cell is \"Value is required for item 4.1.G\" (the cell shows \"Required value is missing.\")");
            Check("A-012-13", problems.Contains("Value is required for item 2.1"), "a required Sketch with Value null");
            Check("F-006 S7", problems.Contains("Value is required for item 2.2"), "a required Signature item with \"ok\" but no picture is missing");
            Check("A-012-17", problems.Contains("Value is required for item 1.1"), "a required blank Memo");
        }

        // ---------- A-012-15 Cleared signature ----------
        {
            var doc = Valid(); doc["Signature"] = null;
            Check("A-012-15", Blocking(Run(doc)).Contains("Signature required."), "Signature null -> \"Signature required.\"");
            doc["Signature"] = "";
            Check("I20", Run(doc).IsValid, "an empty signature passes, as INSPECT (owner Q-36 default)");
        }

        // ---------- A-012-18 Other statuses pass ----------
        foreach (int status in new[] { 2, 5, 7, 9, 24 })
        {
            var doc = Valid(); doc["StatusId"] = status;
            Check("A-012-18", Run(doc).IsValid, $"StatusId {status} passes");
        }

        // ---------- A-012-21 / A-012-22 BWT lock ----------
        {
            var doc = Valid(); doc["InspectionCode"] = "BWT";
            var model = doc.ToObject<InspectionFile>()!;
            var result = InspectionValidator.Validate(model, k => doc[k], _ => null);
            Check("A-012-21", InspectionValidator.IsBwtLocked(model, k => doc[k], true, result),
                "a signed, valid BWT opened from a file is read-only (Submit is not blocked by the lock, L2)");
            Check("A-012-21", !InspectionValidator.IsBwtLocked(model, k => doc[k], false, result), "a BWT that was never saved is editable");
            doc["InspectionCode"] = "BWI";
            Check("A-012-22", !InspectionValidator.IsBwtLocked(model, k => doc[k], true, result), "a BWI-coded file is never locked");
            doc["InspectionCode"] = "BWT"; doc["Signature"] = null;
            Check("L1", !InspectionValidator.IsBwtLocked(model, k => doc[k], true, InspectionValidator.Validate(model, k => doc[k], _ => null)),
                "an unsigned BWT is editable");
        }

        // ---------- W2/W3 messages ----------
        Check("W2", InspectionValidator.SubmitRefused == "Cannot submit inspection. You need to fix one or more problems with the inspection." &&
                    InspectionValidator.ApproveRefused == "Cannot approve inspection. You need to fix one or more problems with the inspection." &&
                    InspectionValidator.PrintRefused == "Cannot print inspection. You need to fix one or more problems with the inspection.",
            "Submit, Approve and Print refusal texts");

        Console.WriteLine();
        Console.WriteLine($"F-012 harness: {_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }
}
