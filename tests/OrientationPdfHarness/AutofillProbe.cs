using InspectionEditor.Models;
using InspectionEditor.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using System.Globalization;
using System.Security.Cryptography;

static class AutofillProbe
{
    static PdfDictionary? Dict(PdfItem? item) => (item is PdfReference reference ? reference.Value : item) as PdfDictionary;
    static IEnumerable<PdfDictionary> Fields(PdfArray? array)
    {
        if (array == null) yield break;
        foreach (var item in array.Elements)
        {
            var field = Dict(item)!;
            yield return field;
            foreach (var child in Fields(field.Elements.GetArray("/Kids"))) yield return child;
        }
    }
    static Dictionary<string, string> Values(byte[] bytes)
    {
        using var doc = PdfReader.Open(new MemoryStream(bytes), PdfDocumentOpenMode.Modify);
        return Fields(doc.Internals.Catalog.Elements.GetDictionary("/AcroForm")?.Elements.GetArray("/Fields"))
            .Where(f => f.Elements.ContainsKey("/T"))
            .GroupBy(f => f.Elements.GetString("/T")).ToDictionary(g => g.Key, g => g.First().Elements.GetString("/V"));
    }
    // Independent expected mapping from the four observed archive families, not production's map.
    static Dictionary<string, string> Expected(JObject ins)
    {
        string Clean(string? text) => string.IsNullOrWhiteSpace(text) || text.Trim().Equals("None", StringComparison.OrdinalIgnoreCase) ? "" : text.Trim();
        var items = ins["Sections"]!.SelectMany(s => s["Items"]!).ToList();
        string Item(string name) => Clean(items.FirstOrDefault(i => (string?)i["Name"] == name)?["Value"]?.ToString());
        string Top(string key) => Clean(ins[key]?.ToString());
        string buyer = Item("Customer Name"); if (buyer == "") buyer = Top("Contact");
        string phone = Item("Customer Phone"); if (phone == "") phone = Top("ContactNumber");
        var date = DateTimeOffset.Parse(Top("DateInspected"), CultureInfo.InvariantCulture);
        var result = new Dictionary<string, string>();
        void Add(string value, params string[] names) { foreach (var name in names) result[name] = value; }
        Add(Top("Project"), "Subdivision_HOI", "Subdivision_FPAF1");
        Add(Top("Lot"), "InspectionLot_HOI", "InspectionLot_FPAF0");
        Add(Top("Block"), "InspectionBlock_HOI");
        Add(Top("Address"), "Address1_HOI", "Address1_LND", "Address1_ATT");
        Add(buyer, "Customer Name_HOI", "Customer Name_LND", "Customer Name_ATT", "Customer Name_FPAF");
        Add(phone, "Customer Phone_HOI", "Customer Phone_FPAF");
        Add(Item("Customer Email"), "Customer Email_FPAF");
        Add(date.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture), "Date_HOI", "Date_FPAF0", "Date_FPAF");
        Add(date.Day.ToString(), "InspectionDay_LND", "InspectionDay_ATT");
        Add(date.ToString("MMMM", CultureInfo.InvariantCulture), "InspectionMonth_LND", "InspectionMonth_ATT");
        Add(date.ToString("yy", CultureInfo.InvariantCulture), "InspectionYear_LND", "InspectionYear_ATT");
        foreach (string name in new[] { "Microwave Serial Number", "Stove/Oven Serial Number", "Dishwasher Serial Number", "Refrigerator Serial Number" })
            Add(Item(name), name == "Stove/Oven Serial Number" ? name : name + "_HOI");
        return result;
    }
    public static byte[] SyntheticPdf(string value)
    {
        using var doc = new PdfDocument();
        var page = doc.AddPage();
        var form = new PdfDictionary(doc); doc.Internals.Catalog.Elements["/AcroForm"] = form;
        var fields = new PdfArray(doc); form.Elements["/Fields"] = fields;
        foreach (var (name, type) in new[] { ("Customer Name_HOI", "/Tx"), ("Unknown", "/Tx"), ("Checkbox", "/Btn") })
        {
            var field = new PdfDictionary(doc);
            field.Elements.SetString("/T", name); field.Elements.SetName("/FT", type);
            field.Elements.SetString("/V", value); field.Elements.SetString("/DA", "/Helv 9 Tf 0 g");
            doc.Internals.AddObject(field); fields.Elements.Add(field.Reference!);
        }
        using var stream = new MemoryStream(); doc.Save(stream); return stream.ToArray();
    }
    public static void Run(string root, string[] args, Action<bool, string> check, Action<Action, string> fails)
    {
        string folder = Path.Combine(root, "probe");
        Directory.CreateDirectory(Path.Combine(folder, "MyList")); Directory.CreateDirectory(Path.Combine(folder, "Documents"));
        string path = Path.Combine(folder, "MyList", "test.ins");
        var json = JObject.Parse("""
        {"InspectionCode":"BWT","Contact":"fallback","ContactNumber":"555-0100","DateInspected":"2026-09-22T23:55:00-05:00","Attachments":[],
        "Sections":[{"Items":[{"Name":"Home orientation form","ControlName":"DocumentButton","Template":"exact.pdf"},
        {"Number":"1.1","Name":"Customer Name","Value":"Current buyer"}]}]}
        """);
        File.WriteAllText(path, json.ToString());
        var saver = new SurgicalSaveService(new FailedSaveRecoveryService(Path.Combine(root, "Recovery")), saveRegistryRoot: root);
        var model = saver.Load(path);
        fails(() => OrientationPdfSession.OpenTemplate(model, path, root), "missing exact template fails without picker");
        string template = Path.Combine(folder, "Documents", "exact.pdf");
        File.WriteAllText(template, "%PDF-1.7\ninvalid content\n%%EOF");
        fails(() => OrientationPdfSession.OpenTemplate(model, path, root), "syntactically corrupt PDF fails despite header and EOF");
        File.WriteAllBytes(template, SyntheticPdf("keep"));
        var session = OrientationPdfSession.OpenTemplate(model, path, root);
        check(model.OrientationEdit == null && model.Attachments!.Count == 0, "template open does not stage model");
        var values = Values(File.ReadAllBytes(session.WorkingPath));
        check(values["Customer Name_HOI"] == "Current buyer" && values["Unknown"] == "keep" && values["Checkbox"] == "keep", "recognized text only, unknowns and checkboxes preserved");
        check(!session.TryFinish(OrientationPdfDecision.Cancel, () => throw new Exception()), "leave Cancel stays without save");
        check(File.Exists(session.WorkingPath), "Cancel retains working session");
        check(!session.TryFinish(OrientationPdfDecision.Save, () => false), "save failure blocks leave");
        check(model.OrientationEdit == null && model.Attachments!.Count == 0, "failed explicit save rolls back staging, retains working PDF for retry");
        check(session.TryFinish(OrientationPdfDecision.Save, () => { saver.Save(model); return true; }), "leave Yes saves and continues");
        model = saver.Load(path);
        session = OrientationPdfSession.OpenEmbedded(model, path, 0, root);
        model.Sections[0].Items[1].Value = "later buyer";
        check(Values(File.ReadAllBytes(session.WorkingPath))["Customer Name_HOI"] == "Current buyer", "embedded prior edits not autofilled");
        model.Sections[0].Items[1].Comments = "ordinary unsaved edit";
        using (var open = new FileStream(session.WorkingPath, FileMode.Open, FileAccess.Read, FileShare.None))
            check(session.TryFinish(OrientationPdfDecision.Discard, () => throw new Exception()), "leave No continues with external file open");
        saver.Save(model);
        check(saver.Load(path).Sections[0].Items[1].Comments == "ordinary unsaved edit", "discard PDF preserves normal report save");
        session = OrientationPdfSession.OpenEmbedded(model, path, 0, root);
        session.Discard(_ => throw new IOException("simulated Windows cleanup lock"));
        check(File.Exists(session.WorkingPath), "cleanup failure is best effort, no exception or save");
        foreach (object? empty in new object?[] { null, "None", "null", "  " })
        {
            model.Sections[0].Items[1].Value = empty; model.Contact = "fallback";
            var prefill = OrientationPdfForm.Fill(SyntheticPdf("keep"), model);
            check(Values(prefill)["Customer Name_HOI"] == "fallback", "empty/sentinel item uses top-level fallback");
            model.Contact = "None";
            check(Values(OrientationPdfForm.Fill(SyntheticPdf("keep"), model))["Customer Name_HOI"] == "keep", "missing values do not write placeholders");
        }
        if (args.Length < 3) return;
        string archive = args[2];
        var paths = Directory.GetFiles(archive, "*BWT*.ins").OrderBy(p => p).ToList();
        var samples = paths.Select(p => (Path: p, Json: JsonConvert.DeserializeObject<JObject>(File.ReadAllText(p), new JsonSerializerSettings { DateParseHandling = DateParseHandling.None })!)).ToList();
        check(samples.Count == 47, "actual archive contains 47 BWT samples");
        check(samples.All(s => s.Json["Attachments"]!.Any(a => a["FileData"] != null)), "all 47 archive samples contain embedded documents");
        Console.WriteLine("Archive families: " + string.Join("; ", samples.GroupBy(s => (string?)s.Json["InspectionName"]).Select(g => $"{g.Key}: {g.Count()}")));
        var selected = samples.Where(s => new[] { "2537336-BWT-2-TL.ins", "2518268-BWT-1-VR.ins", "2555725-BWT-1-AC.ins" }.Contains(Path.GetFileName(s.Path))).ToList();
        selected.Add(samples.First(s => (string?)s.Json["InspectionName"] == "New Home Orientation Beaumont"));
        selected.Insert(0, (args[0], JsonConvert.DeserializeObject<JObject>(File.ReadAllText(args[0]), new JsonSerializerSettings { DateParseHandling = DateParseHandling.None })!));
        foreach (var sample in selected)
        {
            byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(sample.Path));
            var real = new SurgicalSaveService().Load(sample.Path);
            string exact = OrientationPdfSession.FindTemplate(real, sample.Path)!;
            byte[] templateBytes = File.ReadAllBytes(exact);
            var beforeModel = JsonConvert.SerializeObject(real);
            var expected = Expected(sample.Json);
            // For archive assertions compare independent INS mapping directly to archived field values.
            if (sample.Path != args[0])
            {
                var match = OrientationPdfSession.FindCandidates(real).Single();
                byte[] embedded = Convert.FromBase64String(((JObject)real.Attachments![match.Index])["FileData"]!.Value<string>()!);
                var archiveValues = Values(embedded);
                foreach (var pair in expected.Where(p => archiveValues.ContainsKey(p.Key)))
                    check(archiveValues[pair.Key].Trim() == pair.Value, Path.GetFileName(sample.Path) + " archive mapping " + pair.Key);
                var extracted = OrientationPdfSession.OpenEmbedded(real, sample.Path, match.Index, root);
                check(File.ReadAllBytes(extracted.WorkingPath).SequenceEqual(embedded), "archived embedded bytes never re-autofilled");
                extracted.Discard();
                string copyPath = Path.Combine(folder, "MyList", Path.GetFileName(sample.Path));
                var copyJson = (JObject)sample.Json.DeepClone();
                var unrelated = new JObject { ["Filename"] = "unrelated.txt", ["Unknown"] = "keep" };
                ((JArray)copyJson["Attachments"]!).Add(unrelated.DeepClone());
                File.WriteAllText(copyPath, copyJson.ToString());
                var copySaver = new SurgicalSaveService(new FailedSaveRecoveryService(Path.Combine(root, "Recovery")), saveRegistryRoot: root);
                var copyModel = copySaver.Load(copyPath);
                var copySession = OrientationPdfSession.OpenEmbedded(copyModel, copyPath, match.Index, root);
                byte[] edited = embedded.Concat(System.Text.Encoding.ASCII.GetBytes("\n% test-only edited working copy\n")).ToArray();
                File.WriteAllBytes(copySession.WorkingPath, edited);
                check(copySession.TryFinish(OrientationPdfDecision.Save, () => { copySaver.Save(copyModel); return true; }), "archived local-copy edit explicitly saved");
                var diskModel = copySaver.Load(copyPath);
                var disk = (JObject)diskModel.Attachments![match.Index];
                check(Convert.FromBase64String(disk["FileData"]!.Value<string>()!).SequenceEqual(edited), "actual archived edited bytes roundtrip exact");
                var beforeMetadata = (JObject)((JObject)real.Attachments![match.Index]).DeepClone();
                var afterMetadata = (JObject)disk.DeepClone();
                beforeMetadata.Remove("FileData"); afterMetadata.Remove("FileData");
                check(JToken.DeepEquals(beforeMetadata, afterMetadata), "actual archived unknown metadata, dates and page arrays preserved");
                check(JToken.DeepEquals((JObject)diskModel.Attachments.Last(), unrelated), "actual archive-copy unrelated attachment preserved");
            }
            // Fill the actual family's exact blank template in memory (archive attachments remain read-only).
            var filled = OrientationPdfForm.Fill(templateBytes, real);
            var actual = Values(filled);
            var originalValues = Values(templateBytes);
            foreach (var pair in expected.Where(p => actual.ContainsKey(p.Key)))
                check(actual[pair.Key] == (pair.Value == "" ? originalValues[pair.Key] : pair.Value), Path.GetFileName(sample.Path) + " prefill " + pair.Key);
            using var originalDoc = PdfReader.Open(new MemoryStream(templateBytes), PdfDocumentOpenMode.Modify);
            using var filledDoc = PdfReader.Open(new MemoryStream(filled), PdfDocumentOpenMode.Modify);
            check(originalDoc.PageCount == filledDoc.PageCount && originalValues.Count == actual.Count, "all template pages and field identities preserved");
            check(beforeModel == JsonConvert.SerializeObject(real) && real.OrientationEdit == null, "real INS not mutated or staged by fill");
            if (sample.Path == args[0])
            {
                check(exact == args[1], "actual Beaumont resolves supplied exact template");
                foreach (var candidate in OrientationPdfSession.FindCandidates(real))
                {
                    var existing = OrientationPdfSession.OpenEmbedded(real, sample.Path, candidate.Index, root);
                    check(File.ReadAllBytes(existing.WorkingPath).SequenceEqual(Convert.FromBase64String(((JObject)real.Attachments![candidate.Index])["FileData"]!.Value<string>()!)), "current MyList embedded attachment unchanged");
                    existing.Discard();
                }
                // The live MyList can acquire attachments while Trent tests. Simulate the blank
                // state only on a temporary copy, retaining every current item/top-level value.
                string localPath = Path.Combine(folder, "MyList", "real-copy.ins");
                var fresh = (JObject)sample.Json.DeepClone();
                fresh["Attachments"] = new JArray();
                File.WriteAllText(localPath, fresh.ToString());
                File.WriteAllBytes(Path.Combine(folder, "Documents", Path.GetFileName(exact)), templateBytes);
                var localSaver = new SurgicalSaveService(new FailedSaveRecoveryService(Path.Combine(root, "Recovery")), saveRegistryRoot: root);
                var localModel = localSaver.Load(localPath);
                byte[] beforeOpen = File.ReadAllBytes(localPath);
                var working = OrientationPdfSession.OpenTemplate(localModel, localPath, root);
                check(Values(File.ReadAllBytes(working.WorkingPath)).OrderBy(p => p.Key).SequenceEqual(actual.OrderBy(p => p.Key)), "real open uses current INS prefill before editor");
                check(beforeOpen.SequenceEqual(File.ReadAllBytes(localPath)) && localModel.OrientationEdit == null && localModel.Attachments!.Count == 0, "real fresh open does not mutate INS or stage attachment");
                if (args.Length >= 4) { Directory.CreateDirectory(args[3]); File.Copy(working.WorkingPath, Path.Combine(args[3], "beaumont-prefilled.pdf"), true); }
                byte[] prefilled = File.ReadAllBytes(working.WorkingPath);
                check(working.TryFinish(OrientationPdfDecision.Save, () => { localSaver.Save(localModel); return true; }), "real prefilled PDF explicitly saved");
                var reopened = localSaver.Load(localPath);
                var reopenedSession = OrientationPdfSession.OpenEmbedded(reopened, localPath, 0, root);
                check(File.ReadAllBytes(reopenedSession.WorkingPath).SequenceEqual(prefilled), "real prefilled PDF save/reopen byte exact");
                reopenedSession.Discard();
            }
            check(sourceHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(sample.Path))) && templateBytes.SequenceEqual(File.ReadAllBytes(exact)), "read-only source and exact template unchanged");
        }
    }
}
