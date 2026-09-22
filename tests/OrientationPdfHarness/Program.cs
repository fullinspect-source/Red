using InspectionEditor.Models;
using InspectionEditor.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text;

int passed = 0;
void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); passed++; }
void Fails(Action action, string name) { try { action(); } catch (IOException) { Check(true, name); return; } throw new Exception("Expected safe failure: " + name); }
byte[] Pdf(string text) => AutofillProbe.SyntheticPdf(text);
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
    Check(OrientationPdfSession.FindCandidates(model).Count == 0, "blank INS requires exact template, never fake attachment");
    string folder = Path.Combine(root, "Inspections");
    Directory.CreateDirectory(Path.Combine(folder, "MyList")); Directory.CreateDirectory(Path.Combine(folder, "Documents"));
    string path = Path.Combine(folder, "MyList", "report.ins");
    File.WriteAllText(path, blank.ToString());
    string template = Path.Combine(folder, "Documents", OrientationPdfSession.TemplateName(model)!);
    Check(OrientationPdfSession.FindTemplate(model, path) == null, "missing template is unavailable");
    File.WriteAllBytes(template, Pdf("official local template"));
    Check(OrientationPdfSession.FindTemplate(model, path) == template, "exact INS template in sibling Documents resolved");
    model.Sections[0].Items[0].ExtensionData!["Template"] = "../escape.pdf";
    Check(OrientationPdfSession.FindTemplate(model, path) == null, "traversal template rejected");
    model.Sections[0].Items[0].ExtensionData!["Template"] = "C:\\untrusted.pdf";
    Check(OrientationPdfSession.FindTemplate(model, path) == null, "absolute Windows template rejected");
    var saver = new SurgicalSaveService(new FailedSaveRecoveryService(Path.Combine(root,"Recovery")), saveRegistryRoot: root);
    model = saver.Load(path);
    byte[] original = Pdf("blank"); File.WriteAllBytes(template, original);
    var session = OrientationPdfSession.OpenTemplate(model, path, Path.Combine(root, "working"));
    Check(session.WorkingPath != template && File.ReadAllBytes(session.WorkingPath).SequenceEqual(original), "template with no mapped values remains exact");
    Check(Path.GetFileName(session.WorkingPath) == "Orientation.pdf" && Path.IsPathFullyQualified(session.WorkingPath), "constant sanitized PDF working filename");
    Check(!session.StartInfo.Arguments.Any() && session.StartInfo.UseShellExecute && session.StartInfo.FileName == session.WorkingPath, "shell opens only owned PDF, no command arguments");
    Check(session.Capture(model), "explicit capture stages attachment edit");
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
    Check(!File.Exists(session.WorkingPath) && File.ReadAllBytes(template).SequenceEqual(original), "confirmed cleanup never deletes original template");
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
    Check(Convert.FromBase64String(((JObject)saver.Load(path).Attachments![1])["FileData"]!.Value<string>()!).SequenceEqual(File.ReadAllBytes(session.WorkingPath)), "retry preserves pending PDF exactly");
    File.WriteAllBytes(session.WorkingPath, Pdf("late edit"));
    Fails(() => session.Complete(), "late external edit prevents cleanup");
    Check(File.Exists(session.WorkingPath), "late edits retained");
    File.WriteAllText(session.WorkingPath,"not a PDF");
    Fails(() => session.Capture(model), "invalid edited PDF fails closed");
    File.Delete(session.WorkingPath);
    Fails(() => session.Capture(model), "missing working copy fails closed");
    session.Discard();
    var invalid = Model(blank); invalid.Attachments = new() { new JObject { ["Filename"]="Orientation.pdf", ["FileData"]="broken" } };
    Fails(() => OrientationPdfSession.OpenEmbedded(invalid,path,0,root), "corrupt embedded PDF rejected, no silent template replacement");
    invalid.Attachments.Add(((JObject)invalid.Attachments[0]).DeepClone());
    Check(OrientationPdfSession.FindCandidates(invalid).Count == 2, "ambiguous orientation attachments exposed rather than first-wins");

    EmbeddedCorrectionProbe.Run(root, args, Check);
    AutofillProbe.Run(root, args, Check, Fails);
    OrientationControlFlow.OrientationUiProbe.Run(root, Check);
    Console.WriteLine($"{passed} orientation PDF checks passed");
}
finally { Directory.Delete(root,true); }
