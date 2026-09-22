using InspectionEditor.Models;
using InspectionEditor.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text;

int passed = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); passed++; }
void Fails(Action action, string name) { try { action(); } catch (IOException) { Check(true, name); return; } throw new Exception("Expected safe failure: " + name); }
byte[] Pdf(string text) => AutofillProbe.SyntheticPdf(text);
string root = Path.Combine(Path.GetTempPath(), "red-attachment-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    if (args.Contains("--attachment-regressions"))
    {
        AttachmentRegressionProbe.Run(root, Check);
        InspectionEditor.MainWindow.RunAttachmentManagerProbe(root, Check);
        Console.WriteLine($"{passed} attachment regression checks passed");
        return;
    }
    var blank = JObject.Parse("""
    {"InspectionCode":"BWT","InspectionName":"New Home Orientation Beaumont","UnknownRoot":{"keep":42},
     "Sections":[{"Items":[{"ItemId":15847,"Name":"Home orientation form","ControlName":"DocumentButton","Template":"Exact.pdf","Value":"","UnknownItem":"keep","Pictures":[]}]}],"Attachments":[]}
    """);
    var model = blank.ToObject<InspectionFile>()!;
    Check(OrientationPdfSession.IsApplicable(model), "BWT applicability");
    model.InspectionCode = "OTHER"; Check(OrientationPdfSession.IsApplicable(model), "name applicability");
    model.InspectionName = "Framing"; Check(!OrientationPdfSession.IsApplicable(model), "unrelated type excluded");
    model = blank.ToObject<InspectionFile>()!;
    foreach (string name in new[] { "Orientation.pdf", "Other Region.pdf", "Exact (copy).pdf", "Exact (House - 20260230).pdf", "Exact (House - 20260922).pdf.bak", "Exact prefix.pdf", "../Exact.pdf" })
    {
        model.Attachments = new() { new JObject { ["Filename"] = name, ["RedOrientationPdf"] = true } };
        Check(OrientationPdfSession.FindCandidates(model).Count == 0, "strict excludes tagged " + name);
    }
    foreach (string name in new[] { "Exact.pdf", "EXACT (House - 20260922).PDF" })
    {
        model.Attachments = new() { new JObject { ["Filename"] = name } };
        Check(OrientationPdfSession.FindCandidates(model).Single().Index == 0, "official match " + name);
    }
    model.Attachments!.Add(new JObject { ["Filename"] = "Exact.pdf" });
    Fails(() => OrientationPdfSession.PreferredCandidateIndex(model, OrientationPdfSession.FindCandidates(model)), "duplicates fail loudly");
    Fails(() => OrientationPdfSession.OpenEmbedded(model, "report.ins", 0, root), "explicit index cannot bypass ambiguity");
    foreach (string? unsafeName in new string?[] { null, "../Exact.pdf", "C:\\Exact.pdf", "CON.pdf", "LPT1.pdf", "foo.pdf:evil", "foo.pdf ", "foo.pdf.", "foo\n.pdf" })
    {
        model.Sections[0].Items[0].ExtensionData!["Template"] = unsafeName!;
        Fails(() => OrientationPdfSession.FindCandidates(model), "unsafe/missing template blocks even embedded " + unsafeName);
        Check(!PdfAttachmentService.SafeFilename(unsafeName), "unsafe filename rejected " + unsafeName);
    }
    string folder = Path.Combine(root, "Inspections");
    Directory.CreateDirectory(Path.Combine(folder, "MyList")); Directory.CreateDirectory(Path.Combine(folder, "Documents"));
    string path = Path.Combine(folder, "MyList", "report.ins");
    File.WriteAllText(path, blank.ToString());
    var saver = new SurgicalSaveService(new FailedSaveRecoveryService(Path.Combine(root, "Recovery")), saveRegistryRoot: root);
    model = saver.Load(path);
    try { OrientationPdfSession.OpenTemplate(model, path, root); throw new Exception("missing template accepted"); }
    catch (IOException ex) { Check(ex.Message.Contains("Exact.pdf") && ex.Message.Contains("Contact Trent") && ex.Message.Contains("will not substitute"), "blocking error exact filename and guidance"); }
    string template = Path.Combine(folder, "Documents", "Exact.pdf");
    byte[] original = Pdf("original"); File.WriteAllBytes(template, original);
    var session = OrientationPdfSession.OpenTemplate(model, path, root);
    var monitor = session.Monitor;
    Check(File.ReadAllBytes(monitor.WorkingPath).SequenceEqual(original) && monitor.WorkingPath != template, "owned copy exact, source never opened");
    Check(monitor.StartInfo.UseShellExecute && monitor.StartInfo.Arguments == "" && monitor.StartInfo.FileName == monitor.WorkingPath, "shell only owned path no arguments");
    int saves = 0;
    bool Save() { saver.Save(model); saves++; return true; }
    var now = DateTimeOffset.UtcNow;
    Check(!monitor.Poll(now, Save) && saves == 0 && monitor.CanLeave(out _), "unchanged template opens do not save");
    byte[] edited = Pdf("edited"); File.WriteAllBytes(monitor.WorkingPath, edited);
    Check(!monitor.CanLeave(out _) && monitor.HasPendingChanges, "leave rereads unseen change");
    Check(!monitor.Poll(now, Save) && !monitor.Poll(now.AddMilliseconds(499), Save), "deterministic 500ms debounce");
    Check(monitor.Poll(now.AddMilliseconds(500), Save) && saves == 1, "stable complete changed bytes captured once");
    Check(!monitor.Poll(now.AddSeconds(2), Save) && saves == 1, "duplicate event coalesced");
    Check(monitor.AttachmentIndex == 0 && monitor.CanLeave(out _), "new template gains index and clean leave");
    Check(((JObject)model.Attachments![0]).Value<string>("Filename") == "Exact.pdf", "new template uses declared filename");
    Check(!JsonConvert.SerializeObject(model).Contains("AttachmentEdit") && model.AttachmentEdit == null, "staging not serialized and cleared after success");
    var reopened = new SurgicalSaveService().Load(path);
    Check(PdfAttachmentService.EmbeddedPdf((JObject)reopened.Attachments![0]).SequenceEqual(edited), "guarded save/reopen PDF exact");
    Check(File.ReadAllBytes(template).SequenceEqual(original), "template source unchanged");
    var raw = JObject.Parse(File.ReadAllText(path));
    Check((int?)raw["UnknownRoot"]!["keep"] == 42 && (string?)raw["Sections"]![0]!["Items"]![0]!["UnknownItem"] == "keep", "unknown INS metadata preserved");
    Check((string?)raw["Sections"]![0]!["Items"]![0]!["Value"] == "", "document answer not fabricated");
    var beforeModel = PdfAttachmentService.Snapshot(model);
    byte[] next = Pdf("retry"); File.WriteAllBytes(monitor.WorkingPath, next);
    now = now.AddSeconds(3); monitor.Poll(now, Save);
    int failures = 0;
    bool Failure() { failures++; return false; }
    Check(!monitor.Poll(now.AddSeconds(1), Failure) && failures == 1, "guarded save failure recorded");
    Check(JToken.DeepEquals(beforeModel, PdfAttachmentService.Snapshot(model)) && model.AttachmentEdit == null && File.Exists(monitor.WorkingPath), "failed save rolls model back retains working copy");
    Check(!monitor.Poll(now.AddSeconds(2), Failure) && failures == 1 && !monitor.CanLeave(out _), "same failed bytes suppress automatic retry block leave");
    // Retry uses wall clock; make the observed stable interval explicit using a real past timestamp.
    File.WriteAllBytes(monitor.WorkingPath, Pdf("retry changed"));
    monitor.Poll(DateTimeOffset.UtcNow.AddSeconds(-1), Save);
    Check(monitor.RetrySave(Save), "manual stable retry succeeds");
    File.WriteAllText(monitor.WorkingPath, "%PDF-1.7\ntruncated");
    Check(!monitor.Poll(now, Save) && !monitor.CanLeave(out _) && File.Exists(monitor.WorkingPath), "truncated working file blocks leave retained");
    byte[] beforeMalformedDisk = File.ReadAllBytes(path);
    string beforeMalformedModel = JsonConvert.SerializeObject(model);
    int beforeMalformedSaves = saves;
    File.WriteAllText(monitor.WorkingPath, "%PDF-1.7\ninvalid\n%%EOF");
    Check(!monitor.Poll(now, Save), "malformed PDF first poll waits for debounce");
    Check(!monitor.Poll(now.Add(PdfEditMonitor.Debounce), Save) && monitor.Status.Contains("could not be parsed"),
        "stable header EOF alone not accepted as PDF");
    Check(saves == beforeMalformedSaves && File.ReadAllBytes(path).SequenceEqual(beforeMalformedDisk) &&
        JsonConvert.SerializeObject(model) == beforeMalformedModel && model.AttachmentEdit == null,
        "stable malformed PDF never saves and preserves original INS and model");
    Check(File.Exists(monitor.WorkingPath) && File.ReadAllText(monitor.WorkingPath) == "%PDF-1.7\ninvalid\n%%EOF" &&
        !monitor.CanLeave(out _) && monitor.HasPendingChanges,
        "stable malformed PDF retained and blocks leave");
    File.WriteAllBytes(monitor.WorkingPath, Pdf("lock retry"));
    using (var locked = new FileStream(monitor.WorkingPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        Check(!monitor.Poll(now, Save) && !monitor.CanLeave(out _), "locked working file fails closed");
    monitor.Poll(DateTimeOffset.UtcNow.AddSeconds(-1), Save); Check(monitor.RetrySave(Save), "unlock stable retry succeeds");
    byte[] savedDisk = File.ReadAllBytes(path);
    File.WriteAllBytes(monitor.WorkingPath, Pdf("disk conflict"));
    monitor.Poll(DateTimeOffset.UtcNow.AddSeconds(-1), Save);
    File.WriteAllText(path, "{\"external\":true}");
    Check(!monitor.RetrySave(Save) && File.ReadAllText(path) == "{\"external\":true}" && !monitor.CanLeave(out _), "external INS conflict preserves disk blocks leave");
    Check(Directory.GetFiles(Path.Combine(root, "Recovery"), "*.ins", SearchOption.AllDirectories).Any(), "failed complete candidate recovery exists");
    File.WriteAllBytes(path, savedDisk); Check(monitor.RetrySave(Save), "restored conflict retry saves");
    monitor.Cleanup(); Check(!File.Exists(monitor.WorkingPath) && File.Exists(template), "cleanup owns only unique session directory");
    Check(!monitor.Poll(now.AddDays(1), Save), "closed monitor cannot save stale report");

    // General attachments, unknown tokens, Plan Check and multi-monitor rebase.
    raw = JObject.Parse(File.ReadAllText(path));
    var first = (JObject)raw["Attachments"]![0]!;
    first["Unknown"] = new JObject { ["date"] = "2026-09-22T10:12:00-05:00", ["keep"] = new JArray(1,2) };
    byte[] dataUriBytes = PdfAttachmentService.EmbeddedPdf(first);
    first["FileData"] = "data:application/pdf;base64," + Convert.ToBase64String(dataUriBytes);
    first["EditPath"] = "C:\\evil.exe"; first["ServerPath"] = "https://evil/";
    first["AnnotatedPages"] = new JArray(0); first["IncludedPages"] = new JArray();
    var unrelated = new JObject { ["Filename"] = "notes.txt", ["Unknown"] = new JArray(1,"preserve") };
    ((JArray)raw["Attachments"]!).Add(unrelated.DeepClone()); ((JArray)raw["Attachments"]!).Add("unknown-token");
    File.WriteAllText(path, raw.ToString()); model = saver.Load(path);
    var service = new PdfAttachmentService(model);
    Check(service.Enumerate().Count == 1 && service.Enumerate()[0].PageCount == 1, "enumerate valid PDF only with size/pages");
    string input = Path.Combine(root, "add.pdf"); File.WriteAllBytes(input, Pdf("added"));
    Check(service.Add(input, _ => throw new Exception(), Save), "small PDF added no warning");
    Check(service.Add(input, _ => true, Save) && service.Enumerate().Select(r => r.Filename).Distinct().Count() == 3, "duplicate filenames disambiguated");
    var a = service.Open(0,path,root); var b = service.Open(3,path,root);
    Check(a.WorkingPath != b.WorkingPath && !a.StartInfo.FileName.Contains("evil"), "sessions unique ignore metadata paths");
    Check(File.ReadAllBytes(a.WorkingPath).SequenceEqual(dataUriBytes),
        "data:application/pdf;base64 extraction opens exact bytes ignoring untrusted metadata paths");
    File.WriteAllBytes(a.WorkingPath, Pdf("first monitor")); a.Poll(now, Save); Check(a.Poll(now.AddSeconds(1), Save), "first monitor saves");
    File.WriteAllBytes(b.WorkingPath, Pdf("second monitor")); b.Poll(now, Save); Check(b.Poll(now.AddSeconds(1), Save), "unrelated monitor replacement safely rebases");
    var metadata = (JObject)first.DeepClone(); var actual = (JObject)((JObject)model.Attachments![0]).DeepClone();
    metadata.Remove("FileData"); actual.Remove("FileData");
    Check(JToken.DeepEquals(metadata, actual), "replace unknown metadata and page arrays exact");
    Check(JToken.DeepEquals(JToken.FromObject(model.Attachments[1]), unrelated) && JToken.DeepEquals(JToken.FromObject(model.Attachments[2]), new JValue("unknown-token")), "unknown unrelated tokens exact");
    a.Cleanup(); b.Cleanup();
    var deleteBefore = PdfAttachmentService.Snapshot(model);
    Check(!service.Delete(new[] { 3 }, () => false) && JToken.DeepEquals(deleteBefore, PdfAttachmentService.Snapshot(model)), "delete failure rollback");
    Fails(() => service.Delete(new[] { 1 }, Save), "cannot delete non-PDF");
    Check(service.Delete(new[] { 3,4 }, Save) && model.Attachments.Count == 3, "delete exactly selected PDF indices descending");
    model.ExtensionData ??= new(); model.ExtensionData["RedPlanCheck"] = new JObject { ["keep"] = true };
    model.Attachments.Add(PdfAttachmentService.Create("plan.pdf", Pdf("plan")));
    Check(service.Add(input, _ => true, Save) && new SurgicalSaveService().Load(path).Attachments!.Count == 5, "pending Plan Check append preserved alongside transactional add");
    saver.Save(model); Check(new SurgicalSaveService().Load(path).Attachments!.Count == 5, "ordinary repeat save does not duplicate Plan Check append");
    var stale = service.Open(0,path,root); var token = ((JObject)model.Attachments[0]).DeepClone();
    ((JObject)model.Attachments[0])["Unknown"] = "concurrent";
    File.WriteAllBytes(stale.WorkingPath, Pdf("model conflict")); stale.Poll(DateTimeOffset.UtcNow.AddSeconds(-1), Save);
    Check(!stale.RetrySave(Save) && stale.Status.Contains("changed during editing") && File.Exists(stale.WorkingPath), "target model conflict retained clearly");
    model.Attachments[0] = token; Check(stale.RetrySave(Save), "model conflict reconciliation permits retry"); stale.Cleanup();
    // Padded PDF stays parseable; safety gates use exact bytes, not a claimed metadata size.
    byte[] small = Pdf("large");
    byte[] large = new byte[PdfAttachmentService.WarningBytes + 1]; Array.Fill(large, (byte)' ');
    Array.Copy(small, large, small.Length - 6); Encoding.ASCII.GetBytes("%%EOF\n").CopyTo(large, large.Length - 6);
    string largePath = Path.Combine(root,"large.pdf"); File.WriteAllBytes(largePath,large);
    long warning = 0; int countBefore = model.Attachments.Count;
    Check(!service.Add(largePath, n => { warning=n; return false; }, Save) && warning == large.Length && model.Attachments.Count == countBefore, "15MiB warning exact size denial no mutation");
    Check(service.Add(largePath, n => n == large.Length, Save), "15MiB warning override accepted");
    string huge = Path.Combine(root,"huge.pdf"); using(var f = File.Create(huge)) f.SetLength(PdfAttachmentService.MaxPdfBytes + 1);
    Fails(() => service.Add(huge, _ => true, Save), "100MiB hard maximum");
    File.WriteAllText(input, "%PDF-1.7\nnot a pdf\n%%EOF"); Fails(() => service.Add(input,_ => true, Save), "malformed PDF add rejected");
    Fails(() => PdfAttachmentService.EmbeddedPdf(new JObject { ["FileData"]="broken" }), "bad base64 rejected");
    AttachmentRegressionProbe.Run(root, Check);
    InspectionEditor.MainWindow.RunAttachmentManagerProbe(root, Check);
    AutofillProbe.Run(root, args, Check, Fails);
    EmbeddedCorrectionProbe.Run(root, args, Check);
    OrientationControlFlow.OrientationUiProbe.Run(root, Check);
    Console.WriteLine($"{passed} orientation/attachment backend checks passed");
}
finally { Directory.Delete(root,true); }
