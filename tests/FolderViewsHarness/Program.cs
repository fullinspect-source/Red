// F-014 Outbox/Archive views harness (program/features/F-014-folder-views-unsubmit/ACCEPTANCE.md).
//   dotnet run -p:Platform=x64
// Sandbox folders under %TEMP% and synthetic files only (job 1234567, 100 Example St).
using System.Diagnostics;
using System.IO;
using System.Reflection;
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

    private static string Doc(string number, string? lastDb = null, int lastEvent = 30, string signature = "/9j/AA==", bool server = false) =>
        new JObject
        {
            ["InspectionNumber"] = number, ["InspectionCode"] = number.Split('-')[1], ["StatusId"] = 2,
            ["StatusList"] = new JArray(new JObject { ["Id"] = 2, ["Name"] = "Passed" }, new JObject { ["Id"] = 3, ["Name"] = "Failed" }),
            ["Address"] = "100 Example St", ["City"] = "Example City", ["BuilderName"] = "Example Builder", ["Project"] = "Example Project",
            ["DateInspected"] = "2026-10-13T10:00:00-05:00", ["LastDBDate"] = lastDb, ["Signature"] = signature,
            ["Sections"] = new JArray(new JObject { ["Name"] = "S", ["Items"] = new JArray(new JObject { ["Number"] = "1.1", ["DisplayLabel"] = server ? "1.1 A<size=9><color=255, 0, 0> *</color></size>" : "1.1 A" }) }),
            ["Events"] = new JArray(new JObject { ["EventTypeId"] = lastEvent, ["AdditionalInfo"] = lastEvent == 31 ? "Submitting inspection" : null }),
            ["Attachments"] = new JArray(), ["Filename"] = server ? number + ".ins" : null
        }.ToString(Formatting.None);

    public static int Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "red-f014-" + Guid.NewGuid().ToString("N"));
        string IF = Path.Combine(root, "Inspections"), local = Path.Combine(root, "RedLocal");
        string myList = Path.Combine(IF, "MyList"), outbox = Path.Combine(IF, "Outbox"), archive = Path.Combine(IF, "Archive");
        foreach (var d in new[] { myList, outbox, archive, local }) Directory.CreateDirectory(d);
        try
        {
            // ---------- A-014-01 counts; header-only rows ----------
            string bigSignature = Convert.ToBase64String(new byte[26000]);
            for (int i = 0; i < 1173; i++)
                File.WriteAllText(Path.Combine(archive, $"{1000000 + i}-CPP-1-PQE.ins"), Doc($"{1000000 + i}-CPP-1-PQE", "2026-10-13T15:10:29.123-05:00", 31, bigSignature));
            File.WriteAllText(Path.Combine(outbox, "2000001-CPP-1-PQE.ins"), Doc("2000001-CPP-1-PQE"));
            File.WriteAllText(Path.Combine(outbox, "2000002-CPP-1-PQE.ins"), Doc("2000002-CPP-1-PQE"));
            Check("A-014-01", Directory.EnumerateFiles(outbox, "*.ins").Count() == 2 && Directory.EnumerateFiles(archive, "*.ins").Count() == 1173,
                "the view captions count top-level *.ins files: Outbox (2), Archive (1173)");
            var cold = Stopwatch.StartNew();
            Directory.EnumerateFiles(archive, "*.ins").Select(InspectionHeaderReader.Read).ToList();
            cold.Stop();
            var watch = Stopwatch.StartNew();
            var headers = Directory.EnumerateFiles(archive, "*.ins").Select(InspectionHeaderReader.Read).ToList();
            watch.Stop();
            Console.WriteLine($"  (first pass {cold.Elapsed.TotalSeconds:F2} s, second pass {watch.Elapsed.TotalSeconds:F2} s)");
            var h = headers[0]!;
            Check("V1", headers.All(x => x != null && x.ReachedSections) && watch.Elapsed.TotalSeconds < 3,
                $"1,173 header-only reads in {watch.Elapsed.TotalSeconds:F2} s (target under 3 s)");
            Check("L4", h.StatusName == "Passed" && h.ProcessedDisplay == "10/13/2026" && h["Address"] == "100 Example St" && h.HasSignature,
                "Status = StatusList name of StatusId; Processed = LastDBDate as MM/dd/yyyy");
            string longHeader = new JObject { ["InspectionNumber"] = "x", ["Signature"] = Convert.ToBase64String(new byte[200_000]), ["StatusId"] = 3,
                ["StatusList"] = new JArray(new JObject { ["Id"] = 3, ["Name"] = "Failed" }), ["Sections"] = new JArray() }.ToString(Formatting.None);
            string longPath = Path.Combine(root, "long.ins"); File.WriteAllText(longPath, longHeader);
            Check("L3", InspectionHeaderReader.Read(longPath)?.StatusName == "Failed", "a header longer than the prefix is read in full");

            // ---------- A-014-04 Submitted label (format) ----------
            var notSubmitted = InspectionHeaderReader.Parse(Encoding.UTF8.GetBytes(Doc("1234567-CPP-1-PQE")), true)!;
            Check("A-014-04", h.ProcessedDisplay == "10/13/2026" && notSubmitted.ProcessedDisplay == "",
                "LastDBDate 2026-10-13T15:10:29.123-05:00 shows 10/13/2026; null shows nothing (the editor says [Not Submitted])");

            // ---------- A-014-03 read-only folders ----------
            var isReadOnly = typeof(MainWindow).GetMethod("IsReadOnlyFolder", BindingFlags.NonPublic | BindingFlags.Static)!;
            bool RO(string p) => (bool)isReadOnly.Invoke(null, new object[] { p })!;
            Check("A-014-03", RO(Path.Combine(archive, "a.ins")) && RO(Path.Combine(outbox, "a.ins")) && !RO(Path.Combine(myList, "a.ins")),
                "files opened from Outbox or Archive open read-only; My List files do not");

            // ---------- A-014-05 Unsubmit before pickup ----------
            string ob = Path.Combine(outbox, "1234567-CPP-1-PQE.ins");
            File.WriteAllText(ob, Doc("1234567-CPP-1-PQE"));
            byte[] obBytes = File.ReadAllBytes(ob);
            var moved = FolderMoveService.Move(ob, IF, InspectFolder.MyList, _ => true, local, _ => { });
            string ml = Path.Combine(myList, "1234567-CPP-1-PQE.ins");
            Check("A-014-05", moved.Outcome == MoveOutcome.Moved && File.ReadAllBytes(ml).SequenceEqual(obBytes) && !File.Exists(ob),
                "Move to My List: identical bytes in My List, Outbox copy gone");

            // ---------- A-014-06 Unsubmit after pickup ----------
            var gone = FolderMoveService.Move(Path.Combine(outbox, "9999999-CPP-1-PQE.ins"), IF, InspectFolder.MyList, _ => true, local, _ => { });
            Check("A-014-06", gone.Outcome == MoveOutcome.SourceGone && gone.Message ==
                "Strand has already picked up this inspection. When it is processed it will appear in Archive, and you can move it to My List from there.",
                "a row whose Outbox file is gone gives the picked-up message");

            // ---------- A-014-07 byte move of a server-format file ----------
            string serverPath = Path.Combine(archive, "1234567-ME-1-PQE.ins");
            File.WriteAllText(serverPath, Doc("1234567-ME-1-PQE", "2026-10-13T15:10:29.123-05:00", 31, server: true));
            byte[] serverBytes = File.ReadAllBytes(serverPath);
            // ---------- A-014-08 overwrite asks first, with a backup ----------
            string existing = Path.Combine(myList, "1234567-ME-1-PQE.ins");
            File.WriteAllText(existing, "{\"InspectionNumber\":\"1234567-ME-1-PQE\",\"old\":true}");
            string? asked = null;
            var replaced = FolderMoveService.Move(serverPath, IF, InspectFolder.MyList, q => { asked = q; return true; }, local, _ => { });
            Check("A-014-07", replaced.Outcome == MoveOutcome.Moved && File.ReadAllBytes(existing).SequenceEqual(serverBytes),
                "the My List copy is byte-identical to the server-format Archive file");
            Check("A-014-08", asked == "My List already contains '1234567-ME-1-PQE'. Replace it?" &&
                              Directory.GetFiles(Path.Combine(local, FolderMoveService.MovesBackupFolder)).Any(f => File.ReadAllText(f).Contains("\"old\":true")),
                "RED asks first and keeps the replaced My List file in its local backup");
            File.WriteAllText(serverPath, Doc("1234567-ME-1-PQE"));
            var declined = FolderMoveService.Move(serverPath, IF, InspectFolder.MyList, _ => false, local, _ => { });
            Check("A-014-08", declined.Outcome == MoveOutcome.Cancelled && File.Exists(serverPath), "No keeps both files");
            Check("M5", FolderMoveService.Summary(2, 3) == "2 of 3 inspections were moved.", "multi-row summary text");

            // ---------- A-014-09 processed detection ----------
            SubmittedTracker.Record("1234567-CPP-2-PQE", IF, InspectUserKind.Inspector, DateTime.Now.AddMinutes(-90), local);
            string processedArchive = Path.Combine(archive, "1234567-CPP-2-PQE.ins");
            File.WriteAllText(processedArchive, Doc("1234567-CPP-2-PQE", lastEvent: 40));
            Check("S3", !SubmittedTracker.IsProcessed(processedArchive) &&
                        SubmittedTracker.NotReturned(IF, DateTime.Now, local).SequenceEqual(new[] { "1234567-CPP-2-PQE" }),
                "RED's own local Archive copy is never processed; after 60 min it is \"not returned yet\" (S4)");
            File.WriteAllText(processedArchive, Doc("1234567-CPP-2-PQE", "2026-10-18T15:10:29.123-05:00", 31));
            Check("A-014-09", SubmittedTracker.IsProcessed(processedArchive) && SubmittedTracker.NotReturned(IF, DateTime.Now, local).Count == 0 &&
                              InspectionHeaderReader.Read(processedArchive)!.ProcessedDisplay == "10/18/2026",
                "the server copy (LastDBDate, last event 31 \"Submitting inspection\") shows Processed with its date");

            // ---------- S2 / A-014-12 notes per kind ----------
            File.WriteAllText(Path.Combine(outbox, "1234567-CPP-3-PQE.ins"), Doc("1234567-CPP-3-PQE"));
            SubmittedTracker.Record("1234567-CPP-3-PQE", IF, InspectUserKind.Inspector, DateTime.Now.AddMinutes(-31), local);
            Check("S2", SubmittedTracker.OutboxNote("1234567-CPP-3-PQE", IF, InspectUserKind.Inspector, DateTime.Now, local) == "Not picked up yet",
                "an Outbox file older than 30 minutes: \"Not picked up yet\"");
            Check("A-014-12", SubmittedTracker.OutboxNote("1234567-CPP-3-PQE", IF, InspectUserKind.NonDropBox, DateTime.Now, local) == "Not sent yet. Press Sync in the Outbox.",
                "NonDropBox: \"Not sent yet. Press Sync in the Outbox.\"");
            SubmittedTracker.Record("1234567-CPP-P-1-PQE", IF, InspectUserKind.Preinspector, DateTime.Now.AddHours(-3), local);
            Check("A-014-12", !SubmittedTracker.Load(local).Any(e => e.Number == "1234567-CPP-P-1-PQE") &&
                              !SubmittedTracker.NotReturned(IF, DateTime.Now, local).Contains("1234567-CPP-P-1-PQE"),
                "a Preinspector submit is never tracked, so no pickup or not-returned warning appears for it");

            // ---------- A-014-11 no sidecars in Outbox or Archive ----------
            var move2 = FolderMoveService.Move(ml, IF, InspectFolder.Archive, _ => true, local, _ => { });
            Check("A-014-11", move2.Outcome == MoveOutcome.Moved &&
                              Directory.GetFiles(outbox).All(f => f.EndsWith(".ins")) && Directory.GetFiles(archive).All(f => f.EndsWith(".ins")),
                "after the moves Outbox and Archive hold only *.ins files");
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine($"F-014 harness: {_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }
}
