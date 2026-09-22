using InspectionEditor.Models;
using InspectionEditor.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using System.Security.Cryptography;

static class EmbeddedCorrectionProbe
{
    static JObject Json(string path) => JsonConvert.DeserializeObject<JObject>(File.ReadAllText(path),
        new JsonSerializerSettings { DateParseHandling = DateParseHandling.None })!;
    static byte[] Bytes(object attachment) => Convert.FromBase64String(((JObject)attachment).Value<string>("FileData")!);
    static PdfDictionary Dict(PdfItem item) => (PdfDictionary)(item is PdfReference r ? r.Value : item);
    static Dictionary<string, string> Values(byte[] bytes)
    {
        using var pdf = PdfReader.Open(new MemoryStream(bytes), PdfDocumentOpenMode.Modify);
        return pdf.Internals.Catalog.Elements.GetDictionary("/AcroForm")!.Elements.GetArray("/Fields")!
            .Elements.Select(Dict).ToDictionary(f => f.Elements.GetString("/T"), f => f.Elements.GetString("/V"));
    }
    static byte[] MixedPdf()
    {
        using var pdf = PdfReader.Open(new MemoryStream(AutofillProbe.SyntheticPdf("keep")), PdfDocumentOpenMode.Modify);
        var fields = pdf.Internals.Catalog.Elements.GetDictionary("/AcroForm")!.Elements.GetArray("/Fields")!;
        foreach (var (name, type, value) in new (string, string, string?)[] {
            ("Address1_HOI", "/Tx", "  "), ("Customer Phone_HOI", "/Tx", null),
            ("Date_HOI", "/Btn", "Yes"), ("InspectionLot_HOI", "/Tx", "None"),
            ("InspectionBlock_HOI", "/Tx", "null"), ("Subdivision_HOI", "/Tx", "0") })
        {
            var field = new PdfDictionary(pdf);
            field.Elements.SetString("/T", name); field.Elements.SetName("/FT", type);
            if (value != null) field.Elements.SetString("/V", value);
            field.Elements.SetString("/DA", "/Helv 11 Tf 0 g");
            field.Elements.SetInteger("/Ff", 4096);
            fields.Elements.Add(field);
        }
        // Parent-held value must be respected by named descendants; unnamed widget appearances
        // must survive when their field is nonblank, and only blank-filled appearances are cleared.
        var parent = new PdfDictionary(pdf);
        parent.Elements.SetName("/FT", "/Tx"); parent.Elements.SetString("/V", "Inherited edit");
        var child = new PdfDictionary(pdf); child.Elements.SetString("/T", "Address1_LND");
        child.Elements["/AP"] = new PdfDictionary(pdf);
        parent.Elements["/Kids"] = new PdfArray(pdf, child); fields.Elements.Add(parent);
        var buyer = Dict(fields.Elements[0]);
        var widget = new PdfDictionary(pdf); widget.Elements["/AP"] = new PdfDictionary(pdf);
        buyer.Elements["/Kids"] = new PdfArray(pdf, widget);
        using var output = new MemoryStream(); pdf.Save(output); return output.ToArray();
    }
    public static void Run(string root, string[] args, Action<bool, string> check)
    {
        string folder = Path.Combine(root, "correction"); Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "report.ins");
        var json = JObject.Parse("""
        {"InspectionCode":"BWT","Address":"Current address","Contact":"Current buyer","ContactNumber":"555-0100",
        "Project":"Current project","Lot":"10","Block":"20","DateInspected":"2026-09-22",
        "Sections":[{"Items":[{"ItemId":1,"ControlName":"DocumentButton","Name":"Orientation","Template":"Exact Orientation.pdf"}]}],
        "Attachments":[]}
        """);
        byte[] mixed = MixedPdf();
        json["Attachments"] = new JArray(new JObject { ["Filename"] = "Exact Orientation.pdf", ["FileData"] = Convert.ToBase64String(mixed) });
        File.WriteAllText(path, json.ToString());
        var saver = new SurgicalSaveService(new FailedSaveRecoveryService(Path.Combine(folder, "recovery")), saveRegistryRoot: folder);
        var model = saver.Load(path); byte[] before = File.ReadAllBytes(path);
        var session = OrientationPdfSession.OpenEmbedded(model, path, 0, folder);
        byte[] filled = File.ReadAllBytes(session.WorkingPath);
        var values = Values(filled);
        check(values["Address1_HOI"] == model.Address && values["Customer Phone_HOI"] == "555-0100", "embedded whitespace/missing mapped fields filled from current INS");
        check(values["Customer Name_HOI"] == "keep" && values["Unknown"] == "keep" && values["Checkbox"] == "keep" && values["Date_HOI"] == "Yes", "embedded nonblank prior edits, unknowns and even mapped checkboxes preserved");
        check(values["InspectionLot_HOI"] == "None" && values["InspectionBlock_HOI"] == "null" && values["Subdivision_HOI"] == "0", "nonblank PDF sentinels and zero are user values, never treated as missing");
        using (var pdf = PdfReader.Open(new MemoryStream(filled), PdfDocumentOpenMode.Modify))
        {
            var fields = pdf.Internals.Catalog.Elements.GetDictionary("/AcroForm")!.Elements.GetArray("/Fields")!;
            var inherited = Dict(fields.Elements.Last()).Elements.GetArray("/Kids")!.Elements.Select(Dict).Single();
            check(!inherited.Elements.ContainsKey("/V") && inherited.Elements.ContainsKey("/AP"), "inherited nonblank field value/appearance retained");
            check(Dict(Dict(fields.Elements[0]).Elements.GetArray("/Kids")!.Elements[0]).Elements.ContainsKey("/AP"), "prior nonblank widget appearance retained");
            var address = fields.Elements.Select(Dict).Single(f => f.Elements.GetString("/T") == "Address1_HOI");
            check(address.Elements.GetString("/DA") == "/Helv 11 Tf 0 g" && address.Elements.GetInteger("/Ff") == 4096 && pdf.PageCount == 1, "blank-fill preserves formatting, flags and pages");
        }
        check(model.AttachmentEdit == null && Bytes(model.Attachments![0]).SequenceEqual(mixed) && before.SequenceEqual(File.ReadAllBytes(path)), "embedded open changes only working bytes, no source write or staging");
        check(!session.Monitor.Poll(DateTimeOffset.UtcNow, () => throw new Exception()) && session.Monitor.CanLeave(out _), "embedded blank-fill unchanged open never invokes save and can leave");
        session.Monitor.Cleanup();
        check(before.SequenceEqual(File.ReadAllBytes(path)) && Bytes(model.Attachments![0]).SequenceEqual(mixed), "unchanged cleanup leaves original INS and attachment byte-exact");
        session = OrientationPdfSession.OpenEmbedded(model, path, 0, folder);
        filled = AutofillProbe.EditWorking(session.WorkingPath);
        check(AutofillProbe.Capture(session.Monitor, () => { saver.Save(model); return true; }), "external editor save captures complete autofilled PDF");
        session.Monitor.Cleanup();
        check(Bytes(saver.Load(path).Attachments![0]).SequenceEqual(filled), "automatic capture stores exact edited working bytes");

        void Blocked(Action action, string name)
        {
            try { action(); } catch (IOException ex)
            { check(ex.Message.Contains("Contact Trent") && ex.Message.Contains("will not substitute"), name); return; }
            throw new Exception("Expected blocking error: " + name);
        }
        int? Prefer(params string[] names)
        {
            model.Attachments = names.Select(n => (object)new JObject { ["Filename"] = n }).ToList();
            return OrientationPdfSession.PreferredCandidateIndex(model, OrientationPdfSession.FindCandidates(model));
        }
        check(Prefer("Orientation.pdf", "Exact Orientation.pdf") == 1, "exact bare template wins over generic");
        check(Prefer("Exact Orientation (House - 20260922).pdf", "Orientation.pdf") == 0, "normal INSPECT suffix wins regardless of attachment order");
        check(Prefer("Orientation.pdf", "EXACT ORIENTATION (House - 20260922).PDF") == 1, "template filename comparison follows Windows case insensitivity");
        Blocked(() => Prefer("Orientation.pdf", "Exact Orientation.pdf", "Exact Orientation (House - 20260922).pdf"), "two official candidates fail loudly");
        Blocked(() => Prefer("Orientation.pdf", "Exact Orientation (House - 20260922).pdf", "Exact Orientation (House - 20260923).pdf"), "multiple official dates fail loudly, not newest wins");
        foreach (string name in new[] { "Exact Orientation Other Region.pdf", "Exact Orientation (copy).pdf", "Exact Orientation (House - 20260230).pdf", "Exact Orientation (House - 20260922).pdf.bak", "Exact Orientation (House - 20260922).pdf\n" })
            check(Prefer("Orientation.pdf", "Other Orientation.pdf", name) == null, "no official guess for prefix/arbitrary suffix/malformed date: " + name.Trim());
        check(Prefer("Orientation.pdf", "Other Orientation.pdf") == null, "zero official candidates require exact template");
        check(Prefer("Orientation.pdf") == null && Prefer() == null, "single generic never substitutes for exact template");
        if (args.Length < 3) return;
        byte[] source = File.ReadAllBytes(args[0]);
        var realSaver = new SurgicalSaveService(); var real = realSaver.Load(args[0]);
        string beforeModel = JsonConvert.SerializeObject(real);
        var candidates = OrientationPdfSession.FindCandidates(real);
        check(candidates.Count == 1, "current real report has only one eligible official walk candidate");
        var generic = new OrientationPdfSession.Candidate(real.Attachments!.FindIndex(a => a is JObject j && (string?)j["Filename"] == "Orientation.pdf"), "Orientation.pdf");
        check(generic.Index >= 0, "real generic attachment retained but excluded from walk candidates");
        Blocked(() => OrientationPdfSession.OpenEmbedded(real, args[0], generic.Index, folder), "real generic index cannot bypass exact walk contract");
        string stem = Path.GetFileNameWithoutExtension(OrientationPdfSession.TemplateName(real))!;
        var official = candidates.Single(c => c.Filename.StartsWith(stem + " (", StringComparison.Ordinal));
        check(OrientationPdfSession.PreferredCandidateIndex(real, candidates) == official.Index, "actual current MyList automatically selects single exact-template official attachment");
        var ambiguous = Json(args[0]).ToObject<InspectionFile>()!;
        ambiguous.Attachments!.Add(((JObject)ambiguous.Attachments[official.Index]).DeepClone());
        Blocked(() => OrientationPdfSession.PreferredCandidateIndex(ambiguous, OrientationPdfSession.FindCandidates(ambiguous)), "actual official duplicate fails loudly");
        var expected = AutofillProbe.Expected(Json(args[0]));
        byte[] genericBytes = Bytes(real.Attachments![generic.Index]);
        byte[] officialBytes = Bytes(real.Attachments[official.Index]);
        var genericValues = Values(genericBytes);
        var mapped = expected.Keys.Where(genericValues.ContainsKey).ToList();
        check(mapped.Count > 0 && mapped.All(k => string.IsNullOrWhiteSpace(genericValues[k])), "actual generic attachment is blank in every recognized mapped field");
        var genericSession = new PdfAttachmentService(real).Open(generic.Index, args[0], folder);
        byte[] genericWorking = File.ReadAllBytes(genericSession.WorkingPath);
        check(genericWorking.SequenceEqual(genericBytes), "general attachment open retains generic bytes exactly without walk autofill");
        var workingValues = Values(OrientationPdfForm.Fill(genericWorking, real, blankOnly: true));
        foreach (string name in mapped)
            check(workingValues[name] == (expected[name] == "" ? genericValues[name] : expected[name]), "actual embedded working autofill " + name);
        check(genericWorking.SequenceEqual(genericBytes) && beforeModel == JsonConvert.SerializeObject(real) && real.AttachmentEdit == null, "actual generic open does not change source model or stage INS");
        var officialValues = Values(officialBytes);
        check(officialValues.Any(p => expected.ContainsKey(p.Key) && !string.IsNullOrWhiteSpace(p.Value)), "actual official attachment already has nonblank mapped values");
        var officialSession = OrientationPdfSession.OpenEmbedded(real, args[0], official.Index, folder);
        var officialWorking = Values(File.ReadAllBytes(officialSession.WorkingPath));
        check(officialValues.Where(p => !string.IsNullOrWhiteSpace(p.Value)).All(p => officialWorking[p.Key] == p.Value), "actual official retains every existing nonblank value");
        // Deliberately change current INS values in memory to prove prior PDF edits win over mapping.
        real.Address = "Different current address"; real.Contact = "Different current buyer";
        foreach (var item in real.Sections.SelectMany(s => s.Items))
            if (item.Name == "Customer Name") item.Value = "Different current buyer";
        var changedModelSession = OrientationPdfSession.OpenEmbedded(real, args[0], official.Index, folder);
        var changedValues = Values(File.ReadAllBytes(changedModelSession.WorkingPath));
        check(officialValues.Where(p => !string.IsNullOrWhiteSpace(p.Value)).All(p => changedValues[p.Key] == p.Value), "official prior nonblank edits win even when current INS differs");
        changedModelSession.Monitor.Cleanup();
        if (args.Length >= 4)
        {
            Directory.CreateDirectory(args[3]);
            File.WriteAllBytes(Path.Combine(args[3], "generic-embedded-original.pdf"), genericBytes);
            File.WriteAllBytes(Path.Combine(args[3], "generic-embedded-prefilled.pdf"), genericWorking);
            File.WriteAllBytes(Path.Combine(args[3], "official-embedded-original.pdf"), officialBytes);
            File.Copy(officialSession.WorkingPath, Path.Combine(args[3], "official-embedded-working.pdf"), true);
        }
        genericSession.Cleanup(); officialSession.Monitor.Cleanup();
        check(source.SequenceEqual(File.ReadAllBytes(args[0])) && Bytes(real.Attachments[generic.Index]).SequenceEqual(genericBytes) && Bytes(real.Attachments[official.Index]).SequenceEqual(officialBytes), "actual unchanged cleanup preserves original INS and both attachment bytes");
        // Exercise an external editor save only on a local byte-copy with BOTH attachments intact.
        File.WriteAllBytes(path, source); model = saver.Load(path); before = File.ReadAllBytes(path);
        var genericCopyMonitor = new PdfAttachmentService(model).Open(generic.Index, path, folder);
        filled = AutofillProbe.EditWorking(genericCopyMonitor.WorkingPath);
        check(before.SequenceEqual(File.ReadAllBytes(path)) && model.AttachmentEdit == null, "actual two-attachment local copy unchanged before automatic capture");
        check(AutofillProbe.Capture(genericCopyMonitor, () => { saver.Save(model); return true; }), "actual generic editor save captured to local copy only");
        genericCopyMonitor.Cleanup();
        var saved = saver.Load(path);
        check(Bytes(saved.Attachments![generic.Index]).SequenceEqual(filled) && JToken.DeepEquals((JObject)saved.Attachments[official.Index], (JObject)real.Attachments[official.Index]) && saved.Attachments.Count == real.Attachments.Count, "actual automatic capture preserves official attachment and all attachments");
        if (args.Length >= 4)
        {
            string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            File.WriteAllText(Path.Combine(args[3], "embedded-correction.json"), JsonConvert.SerializeObject(new {
                source = args[0], sourceSha256 = Hash(source), sourceUnchanged = source.SequenceEqual(File.ReadAllBytes(args[0])),
                mappedBlankFields = mapped.Count, genericSha256 = Hash(genericBytes), genericWorkingSha256 = Hash(genericWorking),
                officialSha256 = Hash(officialBytes), officialNonblankValuesPreserved = true, automaticCaptureUsedLocalCopyOnly = true,
                preferredFilename = official.Filename, multipleOfficialFailLoudly = true
            }, Formatting.Indented));
        }
    }
}
