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
        "Sections":[{"Items":[{"ControlName":"DocumentButton","Name":"Orientation","Template":"Exact Orientation.pdf"}]}],
        "Attachments":[]}
        """);
        byte[] mixed = MixedPdf();
        json["Attachments"] = new JArray(new JObject { ["Filename"] = "Orientation.pdf", ["FileData"] = Convert.ToBase64String(mixed) });
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
        check(model.OrientationEdit == null && Bytes(model.Attachments![0]).SequenceEqual(mixed) && before.SequenceEqual(File.ReadAllBytes(path)), "embedded open changes only working bytes, no source write or staging");
        check(session.TryFinish(OrientationPdfDecision.Discard, () => throw new Exception()), "embedded blank-fill discard never invokes save");
        check(before.SequenceEqual(File.ReadAllBytes(path)) && Bytes(model.Attachments![0]).SequenceEqual(mixed), "discard leaves original INS and attachment byte-exact");
        session = OrientationPdfSession.OpenEmbedded(model, path, 0, folder);
        filled = File.ReadAllBytes(session.WorkingPath);
        check(session.TryFinish(OrientationPdfDecision.Save, () => { saver.Save(model); return true; }), "explicit Save embeds autofill without requiring external PDF edit");
        check(Bytes(saver.Load(path).Attachments![0]).SequenceEqual(filled), "explicit save stores exact filled working bytes");

        int? Prefer(params string[] names)
        {
            model.Attachments = names.Select(n => (object)new JObject { ["Filename"] = n }).ToList();
            return OrientationPdfSession.PreferredCandidateIndex(model, OrientationPdfSession.FindCandidates(model));
        }
        check(Prefer("Orientation.pdf", "Exact Orientation.pdf") == 1, "exact bare template wins over generic");
        check(Prefer("Exact Orientation (House - 20260922).pdf", "Orientation.pdf") == 0, "normal INSPECT suffix wins regardless of attachment order");
        check(Prefer("Orientation.pdf", "EXACT ORIENTATION (House - 20260922).PDF") == 1, "template filename comparison follows Windows case insensitivity");
        check(Prefer("Orientation.pdf", "Exact Orientation.pdf", "Exact Orientation (House - 20260922).pdf") == null, "two official candidates require explicit selection");
        check(Prefer("Orientation.pdf", "Exact Orientation (House - 20260922).pdf", "Exact Orientation (House - 20260923).pdf") == null, "multiple official dates require explicit selection, not newest wins");
        foreach (string name in new[] { "Exact Orientation Other Region.pdf", "Exact Orientation (copy).pdf", "Exact Orientation (House - 20260230).pdf", "Exact Orientation (House - 20260922).pdf.bak", "Exact Orientation (House - 20260922).pdf\n" })
            check(Prefer("Orientation.pdf", "Other Orientation.pdf", name) == null, "no official guess for prefix/arbitrary suffix/malformed date: " + name.Trim());
        check(Prefer("Orientation.pdf", "Other Orientation.pdf") == null, "zero official candidates require explicit selection");
        check(Prefer("Orientation.pdf") == 0 && Prefer() == null, "single generic and no-attachment paths remain supported");
        if (args.Length < 3) return;
        byte[] source = File.ReadAllBytes(args[0]);
        var realSaver = new SurgicalSaveService(); var real = realSaver.Load(args[0]);
        string beforeModel = JsonConvert.SerializeObject(real);
        var candidates = OrientationPdfSession.FindCandidates(real);
        check(candidates.Count == 2, "current real MyList contains exactly two Orientation candidates");
        var generic = candidates.Single(c => c.Filename == "Orientation.pdf");
        string stem = Path.GetFileNameWithoutExtension(OrientationPdfSession.TemplateName(real))!;
        var official = candidates.Single(c => c.Filename.StartsWith(stem + " (", StringComparison.Ordinal));
        check(OrientationPdfSession.PreferredCandidateIndex(real, candidates) == official.Index, "actual current MyList automatically selects single exact-template official attachment");
        var ambiguous = Json(args[0]).ToObject<InspectionFile>()!;
        ambiguous.Attachments!.Add(((JObject)ambiguous.Attachments[official.Index]).DeepClone());
        check(OrientationPdfSession.PreferredCandidateIndex(ambiguous, OrientationPdfSession.FindCandidates(ambiguous)) == null, "actual official duplicate in local model requires explicit selection");
        var expected = AutofillProbe.Expected(Json(args[0]));
        byte[] genericBytes = Bytes(real.Attachments![generic.Index]);
        byte[] officialBytes = Bytes(real.Attachments[official.Index]);
        var genericValues = Values(genericBytes);
        var mapped = expected.Keys.Where(genericValues.ContainsKey).ToList();
        check(mapped.Count > 0 && mapped.All(k => string.IsNullOrWhiteSpace(genericValues[k])), "actual generic attachment is blank in every recognized mapped field");
        var genericSession = OrientationPdfSession.OpenEmbedded(real, args[0], generic.Index, folder);
        byte[] genericWorking = File.ReadAllBytes(genericSession.WorkingPath);
        var workingValues = Values(genericWorking);
        foreach (string name in mapped)
            check(workingValues[name] == (expected[name] == "" ? genericValues[name] : expected[name]), "actual embedded working autofill " + name);
        check(!genericWorking.SequenceEqual(genericBytes) && beforeModel == JsonConvert.SerializeObject(real) && real.OrientationEdit == null, "actual generic working changes without dirtying/staging INS");
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
        changedModelSession.Discard();
        if (args.Length >= 4)
        {
            Directory.CreateDirectory(args[3]);
            File.WriteAllBytes(Path.Combine(args[3], "generic-embedded-original.pdf"), genericBytes);
            File.WriteAllBytes(Path.Combine(args[3], "generic-embedded-prefilled.pdf"), genericWorking);
            File.WriteAllBytes(Path.Combine(args[3], "official-embedded-original.pdf"), officialBytes);
            File.Copy(officialSession.WorkingPath, Path.Combine(args[3], "official-embedded-working.pdf"), true);
        }
        genericSession.Discard(); officialSession.Discard();
        check(source.SequenceEqual(File.ReadAllBytes(args[0])) && Bytes(real.Attachments[generic.Index]).SequenceEqual(genericBytes) && Bytes(real.Attachments[official.Index]).SequenceEqual(officialBytes), "actual discard preserves original INS and both attachment bytes");
        // Exercise explicit save only on a local byte-copy with BOTH attachments intact.
        File.WriteAllBytes(path, source); model = saver.Load(path); before = File.ReadAllBytes(path);
        session = OrientationPdfSession.OpenEmbedded(model, path, generic.Index, folder);
        filled = File.ReadAllBytes(session.WorkingPath);
        check(before.SequenceEqual(File.ReadAllBytes(path)) && model.OrientationEdit == null, "actual two-attachment local copy unchanged before explicit save");
        check(session.TryFinish(OrientationPdfDecision.Save, () => { saver.Save(model); return true; }), "actual generic blank-fill explicitly saved to local copy only");
        var saved = saver.Load(path);
        check(Bytes(saved.Attachments![generic.Index]).SequenceEqual(filled) && JToken.DeepEquals((JObject)saved.Attachments[official.Index], (JObject)real.Attachments[official.Index]) && saved.Attachments.Count == real.Attachments.Count, "actual explicit save preserves official attachment and all attachments");
        if (args.Length >= 4)
        {
            string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            File.WriteAllText(Path.Combine(args[3], "embedded-correction.json"), JsonConvert.SerializeObject(new {
                source = args[0], sourceSha256 = Hash(source), sourceUnchanged = source.SequenceEqual(File.ReadAllBytes(args[0])),
                mappedBlankFields = mapped.Count, genericSha256 = Hash(genericBytes), genericWorkingSha256 = Hash(genericWorking),
                officialSha256 = Hash(officialBytes), officialNonblankValuesPreserved = true, explicitSaveUsedLocalCopyOnly = true,
                preferredFilename = official.Filename, multipleOfficialRequireSelection = true
            }, Formatting.Indented));
        }
    }
}
