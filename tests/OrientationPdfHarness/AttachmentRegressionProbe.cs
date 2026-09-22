using InspectionEditor.Models;
using InspectionEditor.Services;
using Newtonsoft.Json.Linq;

static class AttachmentRegressionProbe
{
    public static void Run(string root, Action<bool, string> check)
    {
        AttachmentDialog.AttachmentsWindow.Run(root, check);
        CorruptAttachmentProbe.Run(root, check);
        byte[] Pdf(string text) => AutofillProbe.SyntheticPdf(text);
        string path = Path.Combine(root, "regressions.ins");
        var json = JObject.Parse("""
        {"InspectionCode":"BWT","Contact":"Current buyer","Sections":[{"Items":[{"ItemId":1,"Name":"Walk","ControlName":"DocumentButton","Template":"official.pdf"}]}],"Attachments":[]}
        """);
        var attachments = (JArray)json["Attachments"]!;
        attachments.Add(PdfAttachmentService.Create("official.pdf", Pdf("")));
        attachments.Add(new JObject { ["Filename"] = "notes.txt", ["Unknown"] = "untouched" });
        attachments.Add(PdfAttachmentService.Create("general.pdf", Pdf("existing")));
        File.WriteAllText(path, json.ToString());
        var saver = new SurgicalSaveService(new FailedSaveRecoveryService(Path.Combine(root,"regression-recovery")), saveRegistryRoot: root);
        var model = saver.Load(path);
        var service = new PdfAttachmentService(model);
        int saves = 0;
        bool Save() { saver.Save(model); saves++; return true; }
        var selected = service.Open(0, path, root);
        check(AutofillProbe.Values(File.ReadAllBytes(selected.WorkingPath))["Customer Name_HOI"] == "Current buyer",
            "manager-first official walk open fills recognized blank fields");
        check(AutofillProbe.Values(PdfAttachmentService.EmbeddedPdf((JObject)model.Attachments![0]))["Customer Name_HOI"] == "" && selected.CanLeave(out _),
            "manager autofill changes only working baseline, not embedded bytes");
        check(!selected.Poll(DateTimeOffset.UtcNow, Save) && saves == 0, "manager autofill-only open does not save");
        var other = service.Open(2, path, root);
        var monitors = new[] { selected, other };
        byte[] disk = File.ReadAllBytes(path);
        check(!service.Delete(new[] {0}, () => false, monitors), "monitored delete failure reported");
        check(File.Exists(selected.WorkingPath) && File.Exists(other.WorkingPath) && selected.AttachmentIndex == 0 && other.AttachmentIndex == 2 && disk.SequenceEqual(File.ReadAllBytes(path)),
            "failed delete retains every working copy, index and original INS");
        try { service.Delete(new[] {0}, () => throw new IOException("save failed"), monitors); }
        catch (IOException) { }
        check(File.Exists(selected.WorkingPath) && File.Exists(other.WorkingPath) && other.AttachmentIndex == 2,
            "throwing delete retains all sessions and indices");
        byte[] selectedBaseline = File.ReadAllBytes(selected.WorkingPath);
        File.WriteAllBytes(selected.WorkingPath, Pdf("unsaved selected"));
        bool blocked = false;
        try { service.Delete(new[] {0}, () => throw new Exception("save must not run"), monitors); }
        catch (IOException) { blocked = true; }
        check(blocked && File.Exists(selected.WorkingPath) && model.Attachments!.Count == 3, "selected uncaptured disk change blocks deletion before transaction");
        File.WriteAllBytes(selected.WorkingPath, selectedBaseline);
        File.WriteAllBytes(other.WorkingPath, Pdf("unrelated pending"));
        check(service.Delete(new[] {0}, Save, monitors), "clean selected PDF deletion permits unrelated pending editor");
        check(!File.Exists(selected.WorkingPath) && File.Exists(other.WorkingPath) && other.AttachmentIndex == 1 && !other.CanLeave(out _),
            "successful delete cleans only selected and rebases live unrelated pending monitor");
        other.Poll(DateTimeOffset.UtcNow.AddSeconds(-1), Save);
        check(other.RetrySave(Save) && AutofillProbe.Values(PdfAttachmentService.EmbeddedPdf((JObject)saver.Load(path).Attachments![1]))["Customer Name_HOI"] == "unrelated pending",
            "rebased monitor captures later edit to correct persisted attachment");
        check((string?)((JObject)model.Attachments![0])["Unknown"] == "untouched", "delete and rebased edit preserve non-PDF metadata");
        other.Cleanup();

        string input = Path.Combine(root, "pending-source.pdf");
        byte[] addBytes = Pdf("pending add"); File.WriteAllBytes(input, addBytes);
        var pending = service.PrepareAdd(input, _ => true, path, root)!;
        var before = PdfAttachmentService.Snapshot(model);
        check(pending.IsPendingAdd && pending.AttachmentIndex == null && !pending.CanLeave(out _) && pending.WorkingPath != input,
            "new add owns copy and blocks leave even before source bytes change");
        check(!pending.RetrySave(() => false) && pending.Status.Contains("Retry Save") && JToken.DeepEquals(before, PdfAttachmentService.Snapshot(model)),
            "failed add rolls model back with explicit retryable pending-add status");
        File.Delete(input);
        check(File.ReadAllBytes(pending.WorkingPath).SequenceEqual(addBytes) && !pending.CanLeave(out _), "failed add survives vanished source and blocks report leave");
        check(!pending.Poll(DateTimeOffset.UtcNow.AddSeconds(2), () => throw new Exception("automatic retry")), "failed unchanged add does not repeatedly auto-retry");
        check(pending.RetrySave(Save) && !pending.IsPendingAdd && pending.CanLeave(out _), "Retry Save commits retained add and clears pending operation");
        check(PdfAttachmentService.EmbeddedPdf((JObject)saver.Load(path).Attachments![pending.AttachmentIndex!.Value]).SequenceEqual(addBytes), "retained add retry persists exact complete bytes without original source");
        int saved = saves;
        check(!pending.RetrySave(Save) && saves == saved, "successful add retry cannot duplicate attachment");
        pending.Cleanup();

        // A unique official populated form must preserve existing values, whereas ambiguous
        // official forms remain inspectable without silently choosing an autofill target.
        model.Attachments = new() { PdfAttachmentService.Create("official.pdf", Pdf("User value")) };
        var populated = service.Open(0, path, root);
        check(AutofillProbe.Values(File.ReadAllBytes(populated.WorkingPath))["Customer Name_HOI"] == "User value", "manager official form preserves nonblank user value");
        populated.Cleanup();
        model.Attachments.Add(PdfAttachmentService.Create("official.pdf", Pdf("")));
        var ambiguous = service.Open(1, path, root);
        check(AutofillProbe.Values(File.ReadAllBytes(ambiguous.WorkingPath))["Customer Name_HOI"] == "", "manager ambiguous walk form remains inspectable without autofill selection");
        ambiguous.Cleanup();
    }
}
