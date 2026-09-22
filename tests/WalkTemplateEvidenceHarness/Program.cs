using InspectionEditor.Models;
using InspectionEditor.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PdfSharp.Pdf.IO;
using System.Security.Cryptography;

// Real PDFs are READ ONLY. All INS files are synthetic and all writes are beneath
// repository artifacts/work (ignored). No system editor is launched by this test.
string repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
string source = args.Length > 0 ? Path.GetFullPath(args[0]) : "/Users/trentfuller/Library/CloudStorage/Dropbox/Inspections/Documents";
string evidenceRoot = Path.Combine(repo, "artifacts", "work");
string run = Path.Combine(evidenceRoot, "walk-roundtrip-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(run, "Documents"));
Directory.CreateDirectory(Path.Combine(run, "MyList"));
var cases = new[] {
    ("New Home Orientation Beaumont", "DR Horton - New Home Orientation Form Beaumont.pdf", 6),
    ("New Home Orientation Corpus Christi", "DR Horton - New Home Orientation Form Corpus Christi.pdf", 6),
    ("New Home Orientation North Central Texas", "DR Horton - New Home Orientation Form NCT.pdf", 6),
    ("New Home Orientation Louisiana West", "DR Horton Louisiana West - Form and Handbook.pdf", 18)
};
var records = new JArray();
int checks = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
string[] PageSignatures(byte[] bytes) {
    using var doc = PdfReader.Open(new MemoryStream(bytes, writable: false), PdfDocumentOpenMode.Import);
    return doc.Pages.Cast<PdfSharp.Pdf.PdfPage>().Select(p =>
        $"{p.Width.Point:R}x{p.Height.Point:R}/{p.Rotate}/" + Hash(p.Contents.CreateSingleContent().Stream.UnfilteredValue)).ToArray();
}
try {
    string inventoryPath = Path.Combine(evidenceRoot, "walk-template-inventory.json");
    var inventory = JObject.Parse(File.ReadAllText(inventoryPath));
    Check(inventory.Value<bool>("passed"), "complete live inventory prerequisite");
    int realDeclarations = 0;
    foreach (JObject declaration in (JArray)inventory["declarations"]!) {
        string relative = declaration.Value<string>("path")!;
        string realIns = Path.GetFullPath(Path.Combine(source, "..", relative));
        Check(realIns.StartsWith(Path.GetFullPath(Path.Combine(source, "..")) + Path.DirectorySeparatorChar), "inventory path bounded to read-only source");
        byte[] originalIns = File.ReadAllBytes(realIns);
        var model = JsonConvert.DeserializeObject<InspectionFile>(System.Text.Encoding.UTF8.GetString(originalIns))!;
        Check(model.InspectionCode == "BWT" && OrientationPdfSession.TemplateName(model) == declaration.Value<string>("template"), "real INS exact production mapping");
        Check(OrientationPdfSession.FindTemplate(model, realIns) == Path.Combine(source, declaration.Value<string>("template")!), "real INS exact sibling file resolution");
        Check(Hash(File.ReadAllBytes(realIns)) == Hash(originalIns), "real INS before/after hash unchanged");
        realDeclarations++;
    }
    Check(realDeclarations == inventory.Value<int>("declaration_count"), "every real declaration verified by production resolver");
    foreach (var (name, filename, pages) in cases) {
        string originalPath = Path.Combine(source, filename);
        byte[] original = File.ReadAllBytes(originalPath);
        string before = Hash(original);
        try {
            string[] originalPages = PageSignatures(original);
            Check(originalPages.Length == pages, filename + " source pages");
            File.WriteAllBytes(Path.Combine(run, "Documents", filename), original);
            var data = new JObject {
                ["InspectionCode"] = "BWT", ["InspectionName"] = name,
                ["Address"] = "123 Synthetic Evidence Road", ["Project"] = "Synthetic Evidence",
                ["Sections"] = new JArray(new JObject { ["Items"] = new JArray(new JObject {
                    ["ItemId"] = 15847, ["Number"] = "2.28", ["Name"] = "Home orientation form",
                    ["ControlName"] = "DocumentButton", ["Template"] = filename,
                    ["ButtonText"] = "Open Orientation Form", ["Value"] = "", ["Pictures"] = new JArray()
                }) }), ["Attachments"] = new JArray()
            };
            string localIns = Path.Combine(run, "MyList", Guid.NewGuid().ToString("N") + ".ins");
            File.WriteAllText(localIns, data.ToString());
            var saver = new SurgicalSaveService(new FailedSaveRecoveryService(Path.Combine(run, "Recovery")), saveRegistryRoot: run);
            InspectionFile model = saver.Load(localIns);
            Check(OrientationPdfSession.TemplateName(model) == filename, filename + " exact production declaration");
            var session = OrientationPdfSession.OpenTemplate(model, localIns, Path.Combine(run, "owned"));
            byte[] filled = File.ReadAllBytes(session.WorkingPath);
            Check(!filled.SequenceEqual(original), filename + " autofill exercised, not a no-op");
            Check(PageSignatures(filled).SequenceEqual(originalPages), filename + " fill preserves every page content stream/dimension/order");
            Check(session.WorkingPath.StartsWith(Path.Combine(run, "owned") + Path.DirectorySeparatorChar), "owned work path");
            Check(session.StartInfo.FileName == session.WorkingPath && session.StartInfo.UseShellExecute && session.StartInfo.Arguments.Length == 0, "owned shell configuration");
            // Simulate an actual editor save without changing any page: change document metadata.
            byte[] edited;
            using (var pdf = PdfReader.Open(new MemoryStream(filled), PdfDocumentOpenMode.Modify)) {
                pdf.Info.Subject = "Synthetic roundtrip verification";
                using var bytes = new MemoryStream(); pdf.Save(bytes, false); edited = bytes.ToArray();
            }
            File.WriteAllBytes(session.WorkingPath, edited);
            int saves = 0;
            bool Save() { saver.Save(model); saves++; return true; }
            var now = DateTimeOffset.UtcNow;
            Check(!session.Monitor.Poll(now, Save), filename + " debounces changed packet");
            Check(session.Monitor.Poll(now + TimeSpan.FromSeconds(1), Save), filename + " captures changed complete packet: " + session.Monitor.Status);
            Check(!session.Monitor.Poll(now + TimeSpan.FromSeconds(2), Save) && saves == 1, filename + " captures exactly once");
            InspectionFile reopened = saver.Load(localIns);
            var attachment = (JObject)reopened.Attachments!.Single();
            byte[] embedded = Convert.FromBase64String(attachment.Value<string>("FileData")!);
            Check(embedded.SequenceEqual(edited), filename + " surgical save/reload bytes exact");
            Check(PageSignatures(embedded).SequenceEqual(originalPages), filename + " embedded all pages and content preserved");
            Check(attachment.Value<string>("Filename") == filename, filename + " attachment official filename");
            var candidate = OrientationPdfSession.FindCandidates(reopened).Single();
            var opened = OrientationPdfSession.OpenEmbedded(reopened, localIns, candidate.Index, Path.Combine(run, "owned"));
            byte[] reopenedBytes = File.ReadAllBytes(opened.WorkingPath);
            Check(reopenedBytes.SequenceEqual(edited), filename + " embedded reopen preserves bytes and filled values");
            Check(PageSignatures(reopenedBytes).SequenceEqual(originalPages), filename + " open/capture/reopen complete packet");
            Check(Hash(File.ReadAllBytes(originalPath)) == before, filename + " original never changed");
            records.Add(new JObject { ["filename"] = filename, ["pages_source"] = pages,
                ["pages_filled"] = PageSignatures(filled).Length, ["pages_embedded"] = PageSignatures(embedded).Length,
                ["pages_reopened"] = PageSignatures(reopenedBytes).Length,
                ["source_sha256_before"] = before, ["source_sha256_after"] = Hash(File.ReadAllBytes(originalPath)),
                ["edited_sha256"] = Hash(edited), ["embedded_sha256"] = Hash(embedded),
                ["reopened_sha256"] = Hash(reopenedBytes), ["all_page_content_streams_dimensions_order_preserved"] = true });
            opened.Monitor.Cleanup(); session.Monitor.Cleanup();
        } finally { Check(Hash(File.ReadAllBytes(originalPath)) == before, filename + " finally source hash guard"); }
    }
    var proof = new JObject { ["passed"] = true, ["checks"] = checks, ["production_linked"] = true,
        ["real_ins_written"] = false, ["real_templates_written"] = false, ["system_editor_launched"] = false,
        ["real_declarations_verified"] = realDeclarations, ["inventory_manifest_sha256"] = inventory["before_manifest_sha256"],
        ["templates"] = records };
    string output = Path.Combine(evidenceRoot, "walk-template-roundtrip.json");
    File.WriteAllText(output, proof.ToString());
    Console.WriteLine(proof);
    Console.WriteLine("Evidence: " + output);
} finally {
    // Only delete this explicitly owned synthetic run, never a source path.
    if (Directory.Exists(run)) Directory.Delete(run, recursive: true);
}
