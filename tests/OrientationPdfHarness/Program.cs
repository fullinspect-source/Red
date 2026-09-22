using InspectionEditor.Models;
using InspectionEditor.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text;

int passed = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); passed++; }
void Fails(Action action, string name) { try { action(); } catch (IOException) { Check(true, name); return; } throw new Exception("Expected safe failure: " + name); }
byte[] Pdf(string text) => Encoding.ASCII.GetBytes("%PDF-1.4\n% synthetic harness fixture " + text + "\n%%EOF\n");
JObject ReadJson(string path) => JsonConvert.DeserializeObject<JObject>(File.ReadAllText(path),
    new JsonSerializerSettings { DateParseHandling = DateParseHandling.None })!;
string root = Path.Combine(Path.GetTempPath(), "red-orientation-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var blank = JObject.Parse("""
    {"InspectionCode":"BWT","InspectionName":"New Home Orientation Beaumont","UnknownRoot":{"keep":42},
     "Sections":[{"Items":[{"ItemId":15847,"Number":"2.28","Name":"Home orientation form","ControlName":"DocumentButton","Template":"DR Horton - New Home Orientation Form Beaumont.pdf","ButtonText":"Open Orientation Form","Value":"","UnknownItem":"keep","Pictures":[]}]}],"Attachments":[]}
    """);
    InspectionFile Model(JObject json) => json.ToObject<InspectionFile>()!;
    var model = Model(blank);
    Check(OrientationPdfSession.IsApplicable(model), "BWT applicability");
    model.InspectionCode = "OTHER";
    Check(OrientationPdfSession.IsApplicable(model), "New Home Orientation name applicability");
    model.InspectionName = "Framing";
    Check(!OrientationPdfSession.IsApplicable(model), "unrelated type excluded even with document metadata");
    model = Model(blank);
    Check(OrientationPdfSession.FindCandidates(model).Count == 0, "blank INS requires template or import, never fake attachment");
    string folder = Path.Combine(root, "Inspections");
    Directory.CreateDirectory(Path.Combine(folder, "MyList")); Directory.CreateDirectory(Path.Combine(folder, "Documents"));
    string path = Path.Combine(folder, "MyList", "report.ins");
    File.WriteAllText(path, blank.ToString());
    string template = Path.Combine(folder, "Documents", OrientationPdfSession.TemplateName(model)!);
    Check(OrientationPdfSession.FindTemplate(model, path) == null, "missing template falls back to import");
    File.WriteAllBytes(template, Pdf("official local template"));
    Check(OrientationPdfSession.FindTemplate(model, path) == template, "exact INS template in sibling Documents resolved");
    model.Sections[0].Items[0].ExtensionData!["Template"] = "../escape.pdf";
    Check(OrientationPdfSession.FindTemplate(model, path) == null, "traversal template rejected");
    model.Sections[0].Items[0].ExtensionData!["Template"] = "C:\\untrusted.pdf";
    Check(OrientationPdfSession.FindTemplate(model, path) == null, "absolute Windows template rejected");
    var saver = new SurgicalSaveService(new FailedSaveRecoveryService(Path.Combine(root,"Recovery")), saveRegistryRoot: root);
    model = saver.Load(path);
    string import = Path.Combine(root, "original.pdf"); byte[] original = Pdf("blank"); File.WriteAllBytes(import, original);
    var session = OrientationPdfSession.Import(model, path, import, Path.Combine(root, "working"));
    Check(session.WorkingPath != import && File.ReadAllBytes(session.WorkingPath).SequenceEqual(original), "import copies exact bytes, never edits original");
    Check(Path.GetFileName(session.WorkingPath) == "Orientation.pdf" && Path.IsPathFullyQualified(session.WorkingPath), "constant sanitized PDF working filename");
    Check(!session.StartInfo.Arguments.Any() && session.StartInfo.UseShellExecute && session.StartInfo.FileName == session.WorkingPath, "shell opens only owned PDF, no command arguments");
    Check(session.Capture(model), "new import stages explicit attachment edit");
    saver.Save(model);
    Check(model.Attachments!.Count == 1, "one attachment added");
    byte[] edited = Pdf("edited with buyer signature"); File.WriteAllBytes(session.WorkingPath, edited);
    Check(session.Capture(model), "external edit captured");
    saver.Save(model);
    var reopened = saver.Load(path);
    var candidate = OrientationPdfSession.FindCandidates(reopened).Single();
    var extracted = OrientationPdfSession.OpenEmbedded(reopened, path, candidate.Index, Path.Combine(root, "working"));
    Check(File.ReadAllBytes(extracted.WorkingPath).SequenceEqual(edited), "save and reopen recover edited PDF byte for byte");
    Check(extracted.WorkingPath != session.WorkingPath, "isolated per-inspection sessions");
    Check(!session.Capture(model), "unchanged working copy is clean");
    session.Complete(); extracted.Capture(reopened); extracted.Complete();
    Check(!File.Exists(session.WorkingPath) && File.ReadAllBytes(import).SequenceEqual(original), "confirmed cleanup never deletes original import");
    var raw = ReadJson(path);
    Check((int?)raw["UnknownRoot"]!["keep"] == 42 && (string?)raw["Sections"]![0]!["Items"]![0]!["UnknownItem"] == "keep", "unknown INS fields preserved");
    Check((string?)raw["Sections"]![0]!["Items"]![0]!["Value"] == "", "DocumentButton answer not fabricated");

    var existing = (JObject)raw["Attachments"]![0]!;
    existing.Remove("RedOrientationPdf");
    existing["Filename"] = "DR Horton - New Home Orientation Form Beaumont (House - 20260922).pdf";
    existing["EditPath"] = "C:\\untrusted\\other.exe";
    existing["UnknownMetadata"] = new JObject { ["keep"] = "2026-09-22T10:12:00-05:00" };
    existing["AnnotatedPages"] = new JArray(0, 1); existing["ServerPath"] = "server-original";
    existing["FileData"] = "data:application/pdf;base64," + Convert.ToBase64String(edited);
    var unrelated = JObject.Parse("{\"Filename\":\"plan.pdf\",\"Unknown\":{\"keep\":true}}");
    raw["Attachments"] = new JArray(unrelated.DeepClone(), existing.DeepClone());
    File.WriteAllText(path, raw.ToString()); model = saver.Load(path);
    Check(OrientationPdfSession.FindCandidates(model).Single().Index == 1, "official INSPECT filename suffix recognized");
    session = OrientationPdfSession.OpenEmbedded(model, path, 1, Path.Combine(root,"working"));
    Check(File.ReadAllBytes(session.WorkingPath).SequenceEqual(edited), "data URI extraction exact, ignores untrusted EditPath");
    byte[] newer = Pdf("second signature"); File.WriteAllBytes(session.WorkingPath, newer); session.Capture(model);
    saver.Save(model);
    raw = ReadJson(path);
    Check(JToken.DeepEquals(raw["Attachments"]![0], unrelated), "unrelated attachment untouched");
    var after = (JObject)raw["Attachments"]![1]!;
    existing.Remove("FileData"); after.Remove("FileData");
    Check(JToken.DeepEquals(existing, after), "all existing attachment metadata preserved");
    Check(OrientationPdfSession.FindCandidates(saver.Load(path)).Count == 1, "no duplicate on repeated save");
    Fails(() => session.Capture(new InspectionFile()), "stale report owner rejected");
    byte[] saved = File.ReadAllBytes(path);
    File.WriteAllBytes(session.WorkingPath, Pdf("pending conflict")); session.Capture(model);
    File.WriteAllText(path,"{\"external\":true}");
    Fails(() => saver.Save(model), "external INS conflict blocks overwrite");
    Check(File.ReadAllText(path) == "{\"external\":true}" && File.Exists(session.WorkingPath), "failure retains external disk and working edits");
    Check(Directory.GetFiles(Path.Combine(root,"Recovery"), "*.ins", SearchOption.AllDirectories).Any(), "failed full candidate recovered");
    File.WriteAllBytes(path, saved); saver.Save(model);
    Check(Convert.FromBase64String(((JObject)saver.Load(path).Attachments![1])["FileData"]!.Value<string>()!).SequenceEqual(Pdf("pending conflict")), "retry preserves pending PDF exactly");
    File.WriteAllBytes(session.WorkingPath, Pdf("late edit"));
    Fails(() => session.Complete(), "late external edit prevents cleanup");
    Check(File.Exists(session.WorkingPath), "late edits retained");
    File.WriteAllText(session.WorkingPath,"not a PDF");
    Fails(() => session.Capture(model), "invalid edited PDF fails closed");
    File.Delete(session.WorkingPath);
    Fails(() => session.Capture(model), "missing working copy fails closed");
    File.WriteAllBytes(import, Pdf("Save As recovery"));
    session.ReplaceWorkingCopy(import);
    Check(session.Capture(model), "Save As import recovers missing working file");
    saver.Save(model);
    Check(File.ReadAllBytes(import).SequenceEqual(Pdf("Save As recovery")), "Save As source remains unchanged");
    session.Complete();
    File.WriteAllText(import,"not a PDF");
    Fails(() => OrientationPdfSession.Import(model,path,import,root), "non PDF import rejected");
    var invalid = Model(blank); invalid.Attachments = new() { new JObject { ["Filename"]="Orientation.pdf", ["FileData"]="broken" } };
    Fails(() => OrientationPdfSession.OpenEmbedded(invalid,path,0,root), "corrupt embedded PDF rejected, no silent template replacement");
    invalid.Attachments.Add(((JObject)invalid.Attachments[0]).DeepClone());
    Check(OrientationPdfSession.FindCandidates(invalid).Count == 2, "ambiguous orientation attachments exposed rather than first-wins");

    // Optional local real data probe, never copied into source or packages.
    if (args.Length >= 2)
    {
        var real = new SurgicalSaveService().Load(args[0]);
        Check(OrientationPdfSession.IsApplicable(real) && OrientationPdfSession.FindCandidates(real).Count == 0, "real Beaumont fixture has no embedded PDF");
        Check(OrientationPdfSession.FindTemplate(real,args[0]) == args[1], "real Beaumont template discovered");
        var realSession = OrientationPdfSession.Import(real,args[0],args[1],Path.Combine(root,"real"));
        Check(File.ReadAllBytes(realSession.WorkingPath).SequenceEqual(File.ReadAllBytes(args[1])), "real official template copied byte for byte");
    }
    if (args.Length >= 3)
    {
        var real = new SurgicalSaveService().Load(args[2]);
        var match = OrientationPdfSession.FindCandidates(real).Single();
        var realSession = OrientationPdfSession.OpenEmbedded(real,args[2],match.Index,Path.Combine(root,"real-embedded"));
        byte[] bytes = Convert.FromBase64String(((JObject)real.Attachments![match.Index])["FileData"]!.Value<string>()!);
        Check(File.ReadAllBytes(realSession.WorkingPath).SequenceEqual(bytes), "real INSPECT archived attachment extracted exactly");
        string local = Path.Combine(root,"archived-copy.ins"); File.Copy(args[2],local);
        var realSaver = new SurgicalSaveService(new FailedSaveRecoveryService(Path.Combine(root,"Recovery")), saveRegistryRoot: root);
        real = realSaver.Load(local);
        var before = (JObject)((JObject)real.Attachments![match.Index]).DeepClone();
        realSession = OrientationPdfSession.OpenEmbedded(real,local,match.Index,Path.Combine(root,"real-roundtrip"));
        byte[] changed = bytes.Concat(Encoding.ASCII.GetBytes("\n% harness-only external editor change\n")).ToArray();
        File.WriteAllBytes(realSession.WorkingPath,changed); realSession.Capture(real); realSaver.Save(real);
        var disk = (JObject)realSaver.Load(local).Attachments![match.Index];
        Check(Convert.FromBase64String(disk["FileData"]!.Value<string>()!).SequenceEqual(changed), "real archived copy edited PDF roundtrip byte exact");
        before.Remove("FileData"); disk.Remove("FileData");
        Check(JToken.DeepEquals(before,disk), "real archived copy metadata preserved including LastWriteTime and page arrays");
        Check(File.ReadAllBytes(args[2]).SequenceEqual(File.ReadAllBytes(Directory.GetFiles(root,"archived-copy.ins.red-save-backup-*.completed").Single())), "source archived INS unchanged by probe");
    }
    Console.WriteLine($"{passed} orientation PDF checks passed");
}
finally { Directory.Delete(root,true); }
