using InspectionEditor.Models;
using InspectionEditor.Services;
using Newtonsoft.Json.Linq;

static class CorruptAttachmentProbe
{
    public static void Run(string root, Action<bool, string> check)
    {
        string folder = Path.Combine(root, "corrupt", "Inspections");
        Directory.CreateDirectory(Path.Combine(folder, "MyList"));
        Directory.CreateDirectory(Path.Combine(folder, "Documents"));
        string path = Path.Combine(folder, "MyList", "corrupt.ins");
        byte[] pdf = AutofillProbe.SyntheticPdf("fallback must not open");
        string template = Path.Combine(folder, "Documents", "official.pdf");
        File.WriteAllBytes(template, pdf);
        var cases = new (string Label, JToken? Data)[] {
            ("invalid base64", new JValue("not-base64!")),
            ("missing data", null), ("null data", JValue.CreateNull()),
            ("object data", new JObject { ["unexpected"] = true }),
            ("array data", new JArray(1, 2)),
            ("truncated PDF", new JValue(Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes("%PDF-1.7\ntruncated")))),
            ("unparseable PDF", new JValue(Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes("%PDF-1.7\ninvalid\n%%EOF"))))
        };
        foreach (var (label, data) in cases)
        {
            var raw = JObject.Parse("""
            {"InspectionCode":"BWT","UnknownRoot":{"keep":[1,"2026-09-22T10:12:00-05:00"]},
             "Sections":[{"Items":[{"ItemId":1,"Name":"Walk","ControlName":"DocumentButton","Template":"official.pdf","UnknownItem":{"preserve":true}}]}],
             "Attachments":[]}
            """);
            var bad = new JObject { ["Filename"] = label.Contains("PDF") ? "official (House - 20260922).pdf" : "official.pdf", ["Unknown"] = new JArray(1, "retain until confirmed"),
                ["EditPath"] = template, ["ServerPath"] = "https://invalid.example/not-used" };
            if (data != null) bad["FileData"] = data.DeepClone();
            var attachments = (JArray)raw["Attachments"]!;
            attachments.Add(new JObject { ["Filename"] = "notes.txt", ["Custom"] = new JObject { ["keep"] = true } });
            attachments.Add(bad);
            attachments.Add("opaque token");
            attachments.Add(PdfAttachmentService.Create("Orientation.pdf", pdf));
            attachments.Add(new JObject { ["Filename"] = "../unsafe.pdf", ["FileData"] = "bad" });
            attachments.Add(new JObject { ["Filename"] = new JObject { ["bad"] = true } });
            File.WriteAllText(path, raw.ToString());
            var saver = new SurgicalSaveService(new FailedSaveRecoveryService(Path.Combine(root, "corrupt-recovery")), saveRegistryRoot: root);
            var model = saver.Load(path);
            var service = new PdfAttachmentService(model);
            var before = PdfAttachmentService.Snapshot(model);
            byte[] disk = File.ReadAllBytes(path);
            var rows = service.Enumerate();
            check(rows.Count == 2 && rows.Single(r => r.Index == 1).Status.Contains("Damaged"),
                label + ": safe-named damaged official row visible beside valid generic");
            var row = rows.Single(r => r.Index == 1);
            check(!row.CanOpen && rows.Single(r => r.Index == 3).CanOpen && row.PageCount == null && row.SizeBytes < 0, label + ": unreadable size/pages not fabricated");
            void Blocked(Action action, string operation, bool guidance = false)
            {
                try { action(); throw new Exception(operation + " accepted " + label); }
                catch (IOException ex) { check(!guidance || (ex.Message.Contains("ATTACHMENTS") && ex.Message.Contains("remove") && ex.Message.Contains("repair")), label + ": " + operation); }
            }
            check(OrientationPdfSession.FindCandidates(model).Single().Index == 1, label + ": corrupt official identity retained, generic not substituted");
            Blocked(() => service.Open(1, path, root), "manager open blocked", true);
            Blocked(() => OrientationPdfSession.OpenEmbedded(model, path, 1, root), "walk embedded open gives recovery guidance", true);
            Blocked(() => OrientationPdfSession.OpenTemplate(model, path, root), "local template fallback blocked", true);
            check(disk.SequenceEqual(File.ReadAllBytes(path)) && JToken.DeepEquals(before, PdfAttachmentService.Snapshot(model)), label + ": enumeration and blocked opens preserve INS/model");
            foreach (int excluded in new[] { 0, 2, 4, 5 })
                Blocked(() => service.Delete(new[] { excluded }, () => throw new Exception("must not save")), "non-PDF/unsafe/opaque deletion rejected " + excluded);
            model.Attachments!.Add(PdfAttachmentService.Create("official.pdf", pdf));
            Blocked(() => OrientationPdfSession.PreferredCandidateIndex(model, OrientationPdfSession.FindCandidates(model)), "valid duplicate never masks corrupt official");
            model.Attachments.RemoveAt(model.Attachments.Count - 1);
            check(!service.Delete(new[] { 1 }, () => false) && JToken.DeepEquals(before, PdfAttachmentService.Snapshot(model)) && model.AttachmentEdit == null,
                label + ": failed delete rolls back damaged token");
            Blocked(() => service.Delete(new[] { 1 }, () => throw new IOException("save failed")), "throwing save rolls back");
            check(JToken.DeepEquals(before, PdfAttachmentService.Snapshot(model)) && disk.SequenceEqual(File.ReadAllBytes(path)), label + ": throwing delete leaves disk/model unchanged");
            File.WriteAllText(path, raw.ToString() + "\n ");
            byte[] external = File.ReadAllBytes(path);
            Blocked(() => service.Delete(new[] { 1 }, () => { saver.Save(model); return true; }), "external INS conflict blocks deletion");
            check(external.SequenceEqual(File.ReadAllBytes(path)) && JToken.DeepEquals(before, PdfAttachmentService.Snapshot(model)), label + ": conflict preserves external disk and all model tokens");
            File.WriteAllBytes(path, disk);
            check(service.Delete(new[] { 1, 1 }, () => { saver.Save(model); return true; }), label + ": damaged row transaction deletes exact selected index once");
            var after = JObject.Parse(File.ReadAllText(path));
            var expected = (JArray)attachments.DeepClone(); expected.RemoveAt(1);
            check(JToken.DeepEquals(after["Attachments"], expected) && JToken.DeepEquals(after["UnknownRoot"], raw["UnknownRoot"]) &&
                ((JObject)raw["Sections"]![0]!["Items"]![0]!).Properties().All(p =>
                    JToken.DeepEquals(p.Value, after["Sections"]![0]!["Items"]![0]![p.Name])),
                label + ": save/readback preserves unrelated attachments and root/item tokens");
            var reopened = saver.Load(path);
            check(!OrientationPdfSession.FindCandidates(reopened).Any(), label + ": confirmed deletion removes official candidate only");
            var recovery = OrientationPdfSession.OpenTemplate(reopened, path, root);
            check(File.Exists(recovery.WorkingPath) && File.ReadAllBytes(template).SequenceEqual(pdf), label + ": exact template recovery available only after saved deletion");
            recovery.Monitor.Cleanup();
        }
    }
}
