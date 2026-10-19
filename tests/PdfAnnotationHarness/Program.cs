// F-017 PDF annotation harness (program/features/F-017-pdf-annotation/ACCEPTANCE.md).
//   dotnet run -p:Platform=x64
// Sandbox Inspections folder and PDF working root under %TEMP%; generated sample PDFs only (a 12-page plan, a form with
// one AcroForm text field, a page with a Link). The external PDF editor is emulated by writing the working copy.
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using InspectionEditor.Models;
using InspectionEditor.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

return Harness.Run();

static class Harness
{
    private static int _passed, _failed;

    private static void Check(string id, bool ok, string what)
    {
        if (ok) { _passed++; Console.WriteLine($"PASS {id} {what}"); }
        else { _failed++; Console.WriteLine($"FAIL {id} {what}"); }
    }

    private static byte[] Save(PdfDocument document)
    {
        using var stream = new MemoryStream();
        document.Save(stream, false);
        return stream.ToArray();
    }

    private static byte[] Plan(int pages)
    {
        using var document = new PdfDocument();
        for (int i = 0; i < pages; i++)
        {
            var page = document.AddPage();
            using var gfx = PdfSharp.Drawing.XGraphics.FromPdfPage(page);
            gfx.DrawRectangle(PdfSharp.Drawing.XPens.Black, 40 + i, 40, 200, 100); // distinct content per page
        }
        return Save(document);
    }

    private static PdfDictionary Annotation(PdfDocument document, string subtype)
    {
        var annot = new PdfDictionary(document);
        annot.Elements["/Type"] = new PdfName("/Annot");
        annot.Elements["/Subtype"] = new PdfName("/" + subtype);
        annot.Elements["/Rect"] = new PdfArray(document, new PdfReal(50), new PdfReal(50), new PdfReal(150), new PdfReal(150));
        if (subtype == "Ink")
            annot.Elements["/InkList"] = new PdfArray(document, new PdfArray(document, new PdfReal(60), new PdfReal(60), new PdfReal(140), new PdfReal(140)));
        document.Internals.AddObject(annot);
        return annot;
    }

    // The external editor's save: native annotations added to the given 0-based pages, page content untouched.
    private static byte[] AddAnnotations(byte[] pdf, params (int Page, string Subtype)[] marks)
    {
        using var input = new MemoryStream(pdf);
        using var document = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
        foreach (var (pageIndex, subtype) in marks)
        {
            var page = document.Pages[pageIndex];
            var annots = page.Elements.GetArray("/Annots");
            if (annots == null)
            {
                annots = new PdfArray(document);
                page.Elements["/Annots"] = annots;
            }
            annots.Elements.Add(Annotation(document, subtype).Reference!);
        }
        return Save(document);
    }

    private static byte[] ContentBytes(byte[] pdf, int pageIndex)
    {
        using var input = new MemoryStream(pdf);
        using var document = PdfReader.Open(input, PdfDocumentOpenMode.Import);
        var contents = document.Pages[pageIndex].Contents;
        using var bytes = new MemoryStream();
        foreach (var item in contents.Elements)
        {
            var dictionary = item is PdfReference reference ? reference.Value as PdfDictionary : item as PdfDictionary;
            if (dictionary?.Stream != null)
                bytes.Write(dictionary.Stream.UnfilteredValue);
        }
        return bytes.ToArray();
    }

    private static byte[] FormPdf()
    {
        using var document = new PdfDocument();
        var page = document.AddPage();
        var field = new PdfDictionary(document);
        field.Elements["/Type"] = new PdfName("/Annot");
        field.Elements["/Subtype"] = new PdfName("/Widget");
        field.Elements["/FT"] = new PdfName("/Tx");
        field.Elements["/T"] = new PdfString("Address1_text");
        field.Elements["/V"] = new PdfString("100 Example St");
        field.Elements["/Rect"] = new PdfArray(document, new PdfReal(50), new PdfReal(700), new PdfReal(300), new PdfReal(720));
        document.Internals.AddObject(field);
        page.Elements["/Annots"] = new PdfArray(document, field.Reference!);
        var form = new PdfDictionary(document);
        form.Elements["/Fields"] = new PdfArray(document, field.Reference!);
        document.Internals.AddObject(form);
        document.Internals.Catalog.Elements["/AcroForm"] = form.Reference;
        return Save(document);
    }

    private static byte[] LinkPdf()
    {
        using var document = new PdfDocument();
        var page = document.AddPage();
        document.AddPage();
        page.Elements["/Annots"] = new PdfArray(document, Annotation(document, "Link").Reference!);
        return Save(document);
    }

    private static JObject Report() => new()
    {
        ["InspectVersion"] = null, ["WorkOrderId"] = "00000000-0000-0000-0000-000000000017", ["ServiceId"] = 1, ["FormId"] = 20,
        ["InspectionName"] = "Example Inspection", ["InspectionNumber"] = "1234567-CPP-1-PQE", ["InspectionCode"] = "CPP",
        ["Attempt"] = 1, ["StatusId"] = 1, ["JobNumber"] = "1234567", ["Address"] = "100 Example St",
        ["InspectionFee_Secure"] = "PLACEHOLDERCIPHERTEXTA0==",
        ["Sections"] = new JArray(new JObject
        {
            ["SectionId"] = 2, ["Name"] = "Footings", ["ControlName"] = "SectionStandardControl", ["Number"] = "2",
            ["Items"] = new JArray(new JObject { ["ItemId"] = 201, ["Name"] = "Forms", ["Number"] = "2.1", ["ControlName"] = "PassFailNaNi", ["Value"] = "Pass", ["Comments"] = "" })
        }),
        ["Events"] = new JArray(), ["Attachments"] = new JArray()
    };

    private static JObject Read(string path)
    {
        using var reader = new JsonTextReader(new StringReader(File.ReadAllText(path))) { DateParseHandling = DateParseHandling.None };
        return JObject.Load(reader);
    }

    private static bool Transact(InspectionFile owner, JArray replacement, Func<bool> save)
    {
        var expected = (JArray)typeof(PdfAttachmentService).GetMethod("Snapshot", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { owner })!;
        return (bool)typeof(PdfAttachmentService).GetMethod("Transact", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(new PdfAttachmentService(owner), new object[] { expected, replacement, save })!;
    }

    private static bool Capture(PdfEditMonitor monitor, Func<bool> save)
    {
        var t0 = DateTimeOffset.UtcNow;
        monitor.Poll(t0, save);
        return monitor.Poll(t0 + PdfEditMonitor.Debounce + TimeSpan.FromMilliseconds(50), save);
    }

    private static int[] Ints(JToken? token) => token is JArray array ? array.Select(t => (int)t).ToArray() : Array.Empty<int>();

    public static int Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "red-f017-" + Guid.NewGuid().ToString("N"));
        string inspections = Path.Combine(root, "Inspections");
        string workingRoot = Path.Combine(root, "AppData", "PdfAttachments");
        Directory.CreateDirectory(Path.Combine(inspections, "MyList"));
        try
        {
            string insPath = Path.Combine(inspections, "MyList", "1234567-CPP-1-PQE.ins");
            File.WriteAllText(insPath, Report().ToString(Formatting.None), new UTF8Encoding(false));
            var saver = new SurgicalSaveService();
            var model = saver.Load(insPath);
            bool SaveIns() { saver.Save(model); return true; }

            // ---------- A-017-01 / 02 Native annotations, metadata after save ----------
            byte[] plan = Plan(12);
            var replacement = JArray.FromObject(model.Attachments!);
            replacement.Add(JobFilesService.ReferenceAttachment("1234567.pdf", plan, DateTime.Now.AddMinutes(-5)));
            Transact(model, replacement, SaveIns);
            var monitor = new PdfAttachmentService(model).Open(0, insPath, workingRoot);
            byte[] marked = AddAnnotations(plan, (5, "Ink"), (9, "Square"));
            File.WriteAllBytes(monitor.WorkingPath, marked);
            DateTime working = new DateTime(2026, 10, 18, 21, 30, 15, DateTimeKind.Local);
            File.SetLastWriteTime(monitor.WorkingPath, working);
            bool captured = Capture(monitor, SaveIns);
            var a = (JObject)((JArray)Read(insPath)["Attachments"]!)[0];
            byte[] stored = Convert.FromBase64String((string)a["FileData"]!);
            Check("A-017-01", captured && PdfAttachmentService.AnnotatedPages(stored).SequenceEqual(new[] { 5, 9 }) &&
                              ContentBytes(stored, 5).AsSpan().SequenceEqual(ContentBytes(plan, 5)) &&
                              ContentBytes(stored, 9).AsSpan().SequenceEqual(ContentBytes(plan, 9)),
                "Ink on page index 5 and Square on 9 are stored as annotations; those pages' content streams are unchanged");
            Check("A-017-02", Ints(a["AnnotatedPages"]).SequenceEqual(new[] { 5, 9 }) && (bool?)a["ReferenceOnly"] == false &&
                              a["IncludedPages"] is JArray { Count: 0 } && (bool?)a["CanExcludePages"] == true &&
                              (string?)a["LastWriteTime"] == PdfAttachmentService.FormatTime(working),
                $"AnnotatedPages [5,9], ReferenceOnly false, IncludedPages [], CanExcludePages true, LastWriteTime = working copy time ({a["LastWriteTime"]})");
            monitor.Cleanup();

            // ---------- A-017-03 Any change clears ReferenceOnly ----------
            {
                var r = JArray.FromObject(model.Attachments!);
                r[0] = JobFilesService.Refreshed((JObject)r[0], plan, DateTime.Now.AddMinutes(-5));
                Transact(model, r, SaveIns);
                var again = new PdfAttachmentService(model).Open(0, insPath, workingRoot);
                using (var input = new MemoryStream(plan))
                using (var document = PdfReader.Open(input, PdfDocumentOpenMode.Modify))
                {
                    document.Info.Title = "saved without marks";
                    File.WriteAllBytes(again.WorkingPath, Save(document));
                }
                Capture(again, SaveIns);
                var b = (JObject)((JArray)Read(insPath)["Attachments"]!)[0];
                Check("A-017-03", (bool?)b["ReferenceOnly"] == false && b["AnnotatedPages"] is JArray { Count: 0 },
                    "a save with no annotation still sets ReferenceOnly false; AnnotatedPages []");
                again.Cleanup();
            }

            // ---------- A-017-04 Link annotations count ----------
            string linkFile = Path.Combine(root, "link.pdf");
            File.WriteAllBytes(linkFile, LinkPdf());
            var service = new PdfAttachmentService(model);
            // The production add path (AttachmentsWindow → PrepareAdd → the monitor's Retry Save → Create), one file each.
            void AddFromFile(string file)
            {
                var pending = service.PrepareAdd(file, _ => true, insPath, workingRoot)!;
                if (!pending.RetrySave(SaveIns)) throw new Exception("pending add did not save: " + pending.Status);
                pending.Cleanup();
            }
            AddFromFile(linkFile);
            var link = ((JArray)Read(insPath)["Attachments"]!).OfType<JObject>().First(x => (string?)x["Filename"] == "link.pdf");
            Check("A-017-04", Ints(link["AnnotatedPages"]).SequenceEqual(new[] { 0 }), "a page with only a Link annotation counts as annotated (index 0)");

            // ---------- D2 / C1 new attachment shape ----------
            Check("D2", link.Properties().Select(p => p.Name).SequenceEqual(InspectProjectionWriter.AttachmentKeys) &&
                        link["EditPath"]?.Type == JTokenType.Null && link["InspectVersion"]?.Type == JTokenType.Null &&
                        (bool?)link["ReferenceOnly"] == false && (bool?)link["CanExcludePages"] == false && link["IncludedPages"] is JArray { Count: 0 },
                "a PDF added from a file has exactly INSPECT's 11 keys in order, EditPath and InspectVersion null");

            // ---------- A-017-05 Multi-select add ----------
            foreach (string name in new[] { "A.pdf", "B.pdf", "C.pdf" })
                File.WriteAllBytes(Path.Combine(root, name), Plan(1));
            foreach (string name in new[] { "A.pdf", "B.pdf", "C.pdf" }) // the dialog's FileNames, one Add each
                AddFromFile(Path.Combine(root, name));
            var names = ((JArray)Read(insPath)["Attachments"]!).OfType<JObject>().Select(x => (string?)x["Filename"]).ToList();
            Check("A-017-05", names.Contains("A.pdf") && names.Contains("B.pdf") && names.Contains("C.pdf") && names.Count(n => n == "A.pdf") == 1,
                "three selected PDFs become three attachments A, B, C");

            // ---------- A-017-06 No form flattening ----------
            string formFile = Path.Combine(root, "form.pdf");
            File.WriteAllBytes(formFile, FormPdf());
            AddFromFile(formFile);
            var projected = JObject.Parse(Encoding.UTF8.GetString(saver.ProjectCurrent(model).ToUtf8Bytes()));
            var form = ((JArray)projected["Attachments"]!).OfType<JObject>().First(x => (string?)x["Filename"] == "form.pdf");
            byte[] formBytes = Convert.FromBase64String((string)form["FileData"]!);
            using (var input = new MemoryStream(formBytes))
            using (var document = PdfReader.Open(input, PdfDocumentOpenMode.Import))
            {
                var acro = document.Internals.Catalog.Elements.GetDictionary("/AcroForm");
                var fields = acro?.Elements.GetArray("/Fields");
                Check("A-017-06", fields != null && fields.Elements.Count == 1 && formBytes.AsSpan().SequenceEqual(File.ReadAllBytes(formFile)),
                    "the form's AcroForm field is still there in the projected FileData; the bytes are exactly the file's");
            }

            // ---------- A-017-08 Projection field set ----------
            {
                var withExtras = JArray.FromObject(model.Attachments!);
                var extra = (JObject)withExtras[1];
                extra["ServerPath"] = null; extra["ReducedFileData"] = null; extra["EditPath"] = "legacy.pdf";
                Transact(model, withExtras, SaveIns);
                var p = JObject.Parse(Encoding.UTF8.GetString(saver.ProjectCurrent(model).ToUtf8Bytes()));
                Check("A-017-08", ((JArray)p["Attachments"]!).OfType<JObject>().All(x => x.Properties().Select(q => q.Name).SequenceEqual(InspectProjectionWriter.AttachmentKeys)) &&
                                  InspectProjectionWriter.AttachmentKeys.SequenceEqual(new[] { "InspectVersion", "Id", "EditPath", "FileData", "FileType", "Filename", "AnnotatedPages", "IncludedPages", "ReferenceOnly", "LastWriteTime", "CanExcludePages" }),
                    "projection writes exactly the 11 keys in INSPECT's order; ServerPath and ReducedFileData are dropped");
            }

            // ---------- A-017-10 PreInspector blocks Attachments ----------
            Check("A-017-10", JobFilesService.IsBlockedFor("PreInspector") && !JobFilesService.IsBlockedFor("Preinspector") &&
                              !JobFilesService.IsBlockedFor("Inspector") && !JobFilesService.IsBlockedFor("NonDropBoxInspector") &&
                              JobFilesService.PreInspectorBlockedMessage == "This functionality is not available in PreInspector mode.",
                "only the exact value PreInspector is blocked, with INSPECT's message");

            // ---------- A1 PDF app from Settings ----------
            {
                string work = @"C:\Work\Orientation.pdf";
                string acro = Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\Adobe\Acrobat Reader DC\Reader\AcroRd32.exe");
                string foxit = Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\Foxit Software\Foxit Reader\FoxitReader.exe");
                var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { acro, foxit };
                var acrobat = PdfAppLauncher.StartInfoFor(work, "AcrobatDC", installed.Contains);
                var fox = PdfAppLauncher.StartInfoFor(work, "FoxitReader", installed.Contains);
                var edgeMissing = PdfAppLauncher.StartInfoFor(work, "MicrosoftEdge", installed.Contains);
                var drawboard = PdfAppLauncher.StartInfoFor(work, "DrawboardPdf", installed.Contains);
                var system = PdfAppLauncher.StartInfoFor(work, "(default)", installed.Contains);
                var acrobatMissing = PdfAppLauncher.StartInfoFor(work, "AcrobatDC", _ => false);
                Check("A1", acrobat.FileName == acro && acrobat.Arguments == "/n \"C:\\Work\\Orientation.pdf\"" && !acrobat.UseShellExecute &&
                            fox.FileName == foxit && fox.Arguments == "\"C:\\Work\\Orientation.pdf\"",
                    "AcrobatDC opens with /n, FoxitReader opens the working copy");
                Check("A1", new[] { edgeMissing, drawboard, system, acrobatMissing }.All(s => s.UseShellExecute && s.FileName == work),
                    "DrawboardPdf, (default) and an app that is not installed use the system .pdf handler on the working copy");
            }
        }
        catch (Exception ex)
        {
            Check("harness", false, "unexpected exception: " + ex);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine($"F-017 harness: {_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }
}
