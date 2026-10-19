// F-022 report preview harness (program/features/F-022-report-preview/ACCEPTANCE.md).
//   dotnet run -p:Platform=x64
// Synthetic fixtures only (no real inspections, names or account lists). The owner-supplied texts and account groups are
// test values set in a sandbox ReportSettings. The produced PDF is read back with PDFium (text, character boxes, pixels)
// and PDFsharp (page sizes, annotations).
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Docnet.Core;
using Docnet.Core.Models;
using InspectionEditor.Services;
using InspectionEditor.Services.Reports;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

return Harness.Run();

static class Harness
{
    private static int _pass, _fail;
    private static readonly DateTime PrintDate = new(2026, 10, 19, 9, 0, 0);
    private const string GroupA = "aaaaaaaa-0000-4000-8000-00000000000a", DfwEast = "bbbbbbbb-0000-4000-8000-00000000000b", Other = "cccccccc-0000-4000-8000-00000000000c";

    private static void Check(string id, bool ok, string what)
    {
        if (ok) _pass++; else _fail++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {id}  {what}");
    }

    // ── Fixtures ───────────────────────────────────────────────────────────────────────────────────────────────────

    private static byte[] Png(int w, int h, byte r, byte g, byte b)
    {
        var pixels = new byte[w * h * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = b; pixels[i + 1] = g; pixels[i + 2] = r; pixels[i + 3] = 255; }
        var bitmap = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static readonly string Blue = Convert.ToBase64String(Png(320, 180, 0, 0, 255));   // 16:9
    private static readonly string Green = Convert.ToBase64String(Png(200, 100, 0, 160, 0));
    private static readonly string Ink = Convert.ToBase64String(Png(300, 60, 20, 20, 20));

    private static JObject Item(string number, string name, string value, string comments = "", int pictures = 0, bool alwaysPrint = false,
        string control = "PassFailNaNi", string? picture = null) => new()
    {
        ["Number"] = number, ["Name"] = name, ["Value"] = value, ["Comments"] = comments, ["ControlName"] = control,
        ["AlwaysPrint"] = alwaysPrint, ["Incidents"] = 0, ["OnlyPrint"] = null,
        ["Pictures"] = new JArray(Enumerable.Range(0, pictures).Select(_ => new JObject { ["Image"] = picture ?? Blue }))
    };

    private static JObject Section(string number, string name, IEnumerable<JObject> items) =>
        new() { ["Number"] = number, ["Name"] = name, ["Items"] = new JArray(items) };

    private static JObject Doc(int formId, params JObject[] sections) => new()
    {
        ["FormId"] = formId, ["StatusId"] = 3, ["InspectionName"] = "Concrete Pre Pour", ["InspectionNumber"] = "1234567-CPP-1-PQE",
        ["Address"] = "100 Example St", ["City"] = "Example City", ["Project"] = "Example Project", ["Lot"] = "5", ["Block"] = "B",
        ["PlanNumber"] = "P-1", ["InspectorName"] = "Inspector One", ["InspectorPhone"] = "555-0100", ["BuilderName"] = "Example Builder",
        ["JobNumber"] = "1234567", ["StructureTypeId"] = 0, ["NextActionText"] = "Failed items to be inspected at next inspection",
        ["DateInspected"] = "2026-10-18T10:30:00", ["Signature"] = Ink, ["IsPreinspection"] = false, ["IsReinspection"] = false,
        ["Sections"] = new JArray(sections), ["Attachments"] = new JArray()
    };

    private static ReportSettings Settings()
    {
        var s = new ReportSettings { CompanyName = "Example Co", CompanyPhone = "555-0101", BuilderBName = "Builder B" };
        for (int i = 1; i <= 12; i++) s.ApprovedTexts["T" + i] = $"TEST TEXT T{i}.";
        s.ApprovedTexts["T1"] = "TEST INTRO {0} for {1}.";
        s.ApprovedTexts["T2"] = "I met with {0} representing {1} for this New Home Orientation. TEST TEXT T2.";
        s.ApprovedTexts["T4"] = "The above items have been satisfactorily completed and the home is ready to close & occupy.";
        s.ApprovedTexts["T9"] = "ACKNOWLEDGEMENT OF COMPLETION: Any and all Punch List Items noted above are complete.";
        s.BuilderGroupAAccounts.Add(GroupA);
        s.BuilderDfwEastAccounts.Add(DfwEast);
        return s;
    }

    private static readonly ReportOptions AllOn = new(false, true, true);

    private static ReportService.Rendered Render(JObject doc, ReportOptions? options = null, ReportSettings? settings = null) =>
        ReportService.Render((JObject)doc.DeepClone(), settings ?? Settings(), options ?? AllOn, PrintDate);

    private static byte[] Plan(params (double W, double H)[] pages)
    {
        using var document = new PdfDocument();
        foreach (var (w, h) in pages)
        {
            var page = document.AddPage();
            page.Width = PdfSharp.Drawing.XUnit.FromPoint(w);
            page.Height = PdfSharp.Drawing.XUnit.FromPoint(h);
            using var g = PdfSharp.Drawing.XGraphics.FromPdfPage(page);
            g.DrawRectangle(PdfSharp.Drawing.XPens.Black, 20, 20, 50, 50);
        }
        using var stream = new MemoryStream();
        document.Save(stream, false);
        return stream.ToArray();
    }

    private static JObject Attachment(string name, byte[] pdf, bool referenceOnly = false, bool canExclude = false, int[]? annotated = null) => new()
    {
        ["Filename"] = name, ["FileType"] = "pdf", ["FileData"] = Convert.ToBase64String(pdf), ["ReferenceOnly"] = referenceOnly,
        ["CanExcludePages"] = canExclude, ["AnnotatedPages"] = new JArray(annotated ?? Array.Empty<int>()), ["IncludedPages"] = new JArray()
    };

    // ── Reading the PDF back ───────────────────────────────────────────────────────────────────────────────────────

    private static string Squeeze(string s) => new string(s.Where(c => !char.IsWhiteSpace(c)).ToArray());

    private static int Pages(byte[] pdf) { using var r = DocLib.Instance.GetDocReader(pdf, new PageDimensions(1.0)); return r.GetPageCount(); }

    private static string Text(byte[] pdf, int page)
    {
        using var r = DocLib.Instance.GetDocReader(pdf, new PageDimensions(1.0));
        using var p = r.GetPageReader(page);
        return p.GetText();
    }

    private static string AllText(byte[] pdf) => string.Join("\n", Enumerable.Range(0, Pages(pdf)).Select(i => Text(pdf, i)));

    private static List<Character> Chars(byte[] pdf, int page)
    {
        using var r = DocLib.Instance.GetDocReader(pdf, new PageDimensions(1.0));
        using var p = r.GetPageReader(page);
        return p.GetCharacters().ToList();
    }

    // Character boxes of the first occurrence of text on a page (PDFium coordinates, 1 px = 1 pt, origin top-left).
    private static List<Character>? Find(byte[] pdf, int page, string text)
    {
        var chars = Chars(pdf, page).Where(c => !char.IsWhiteSpace(c.Char)).ToList();
        string squeezed = new string(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
        string all = new string(chars.Select(c => c.Char).ToArray());
        int at = all.IndexOf(squeezed, StringComparison.Ordinal);
        return at < 0 ? null : chars.GetRange(at, squeezed.Length);
    }

    private sealed record Raster(int W, int H, byte[] Bgra)
    {
        public (int R, int G, int B) At(int x, int y) { int i = (y * W + x) * 4; return (Bgra[i + 2], Bgra[i + 1], Bgra[i]); }
    }

    private static Raster Raster0(byte[] pdf, int page, double scale = 1.0)
    {
        using var r = DocLib.Instance.GetDocReader(pdf, new PageDimensions(scale));
        using var p = r.GetPageReader(page);
        byte[] bytes = p.GetImage();
        for (int i = 0; i + 3 < bytes.Length; i += 4)
        {
            int a = bytes[i + 3];
            for (int k = 0; k < 3; k++) bytes[i + k] = (byte)((bytes[i + k] * a + 255 * (255 - a)) / 255);
            bytes[i + 3] = 255;
        }
        return new Raster(p.GetPageWidth(), p.GetPageHeight(), bytes);
    }

    private static bool Near((int R, int G, int B) c, int r, int g, int b, int tolerance = 12) =>
        Math.Abs(c.R - r) <= tolerance && Math.Abs(c.G - g) <= tolerance && Math.Abs(c.B - b) <= tolerance;

    // Does any glyph of these characters show the colour? (4x raster, so strokes are solid.)
    private static bool Coloured(byte[] pdf, int page, List<Character> chars, int r, int g, int b)
    {
        var raster = Raster0(pdf, page, 4.0);
        foreach (var c in chars)
            for (int y = c.Box.Top * 4; y <= c.Box.Bottom * 4 && y < raster.H; y++)
                for (int x = c.Box.Left * 4; x <= c.Box.Right * 4 && x < raster.W; x++)
                    if (Near(raster.At(x, y), r, g, b, 25)) return true;
        return false;
    }

    // Connected boxes of one colour, found by scanning rows (enough for separated solid test pictures).
    private static List<(int X0, int Y0, int X1, int Y1)> ColourBoxes(Raster raster, int r, int g, int b)
    {
        var seen = new bool[raster.W * raster.H];
        var boxes = new List<(int, int, int, int)>();
        for (int y = 0; y < raster.H; y++)
            for (int x = 0; x < raster.W; x++)
            {
                if (seen[y * raster.W + x] || !Near(raster.At(x, y), r, g, b, 20)) continue;
                int x0 = x, y0 = y, x1 = x, y1 = y;
                var stack = new Stack<(int, int)>();
                stack.Push((x, y));
                seen[y * raster.W + x] = true;
                while (stack.Count > 0)
                {
                    var (cx, cy) = stack.Pop();
                    x0 = Math.Min(x0, cx); x1 = Math.Max(x1, cx); y0 = Math.Min(y0, cy); y1 = Math.Max(y1, cy);
                    foreach (var (nx, ny) in new[] { (cx + 1, cy), (cx - 1, cy), (cx, cy + 1), (cx, cy - 1) })
                    {
                        if (nx < 0 || ny < 0 || nx >= raster.W || ny >= raster.H || seen[ny * raster.W + nx]) continue;
                        if (!Near(raster.At(nx, ny), r, g, b, 20)) continue;
                        seen[ny * raster.W + nx] = true;
                        stack.Push((nx, ny));
                    }
                }
                if ((x1 - x0) > 10 && (y1 - y0) > 10) boxes.Add((x0, y0, x1, y1));
            }
        return boxes;
    }

    // Any non-white pixel outside [left, right) × [top, bottom)?
    private static bool InkOutside(Raster raster, double left, double top, double right, double bottom)
    {
        for (int y = 0; y < raster.H; y++)
            for (int x = 0; x < raster.W; x++)
            {
                if (x >= left - 1 && x < right + 1 && y >= top - 1 && y < bottom + 1) continue;
                var c = raster.At(x, y);
                if (c.R < 245 || c.G < 245 || c.B < 245) return true;
            }
        return false;
    }

    private static (double W, double H) PageSize(byte[] pdf, int page)
    {
        using var document = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Import);
        return (document.Pages[page].Width.Point, document.Pages[page].Height.Point);
    }

    private static int ImageCount(byte[] pdf, int page)
    {
        using var document = PdfReader.Open(new MemoryStream(pdf), PdfDocumentOpenMode.Import);
        var xobjects = document.Pages[page].Resources?.Elements.GetDictionary("/XObject");
        return xobjects?.Elements.Count ?? 0;
    }

    private static IEnumerable<JObject> Rows(int count, Func<int, JObject>? make = null) =>
        Enumerable.Range(1, count).Select(i => make?.Invoke(i) ?? Item($"1.{i}", $"Row item {i}", "Pass"));

    private static readonly bool Debugging = Environment.GetEnvironmentVariable("RED_REPORT_DEBUG") == "1";

    private static void Debug(byte[] pdf, int page, string label)
    {
        if (!Debugging) return;
        var raster = Raster0(pdf, page);
        Console.WriteLine($"DEBUG {label} page {page}: size {raster.W}x{raster.H}");
        int shown = 0;
        for (int y = 0; y < raster.H && shown < 12; y++)
            for (int x = 0; x < raster.W && shown < 12; x++)
            {
                if (x >= 35 && x < 577 && y >= 35 && y < 757) continue;
                var c = raster.At(x, y);
                if (c.R < 245 || c.G < 245 || c.B < 245) { Console.WriteLine($"   outside ink at {x},{y} = {c}"); shown++; x += 40; }
            }
        for (int y = 725; y < 760; y++) { var c = raster.At(300, y); if (c.R < 250 || c.G < 250 || c.B < 250) Console.WriteLine($"   x300 y{y} = {c}"); }
        foreach (var c in Chars(pdf, page).Where(c => c.Box.Top > 700)) Console.Write($"[{c.Char} {c.Box.Left},{c.Box.Top},{c.Box.Right},{c.Box.Bottom}]");
        Console.WriteLine();
        string text = Text(pdf, page).Replace("\r", " ").Replace("\n", " | ");
        Console.WriteLine("   text: " + text.Substring(0, Math.Min(700, text.Length)));
    }

    // The real ReportPreviewWindow on an STA thread, shown off-screen; INSPECT-format log lines go to a sandbox writer.
    private static (int Pages, bool TempExisted, bool TempGone, string Status) PreviewWindowCheck(JObject doc, string root)
    {
        InspectFormatLog.Writer = new InspectLogWriter(Path.Combine(root, "logs"), Path.Combine(root, "logs-pending"));
        (int, bool, bool, string) result = (0, false, false, "");
        var thread = new Thread(() =>
        {
            var window = new InspectionEditor.ReportPreviewWindow((JObject)doc.DeepClone(), Settings(), null)
            {
                WindowStartupLocation = System.Windows.WindowStartupLocation.Manual, Left = -20000, Top = -20000, ShowActivated = false, ShowInTaskbar = false
            };
            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
            var pages = (System.Windows.Controls.StackPanel)typeof(InspectionEditor.ReportPreviewWindow).GetField("_pages", flags)!.GetValue(window)!;
            var status = (System.Windows.Controls.TextBlock)typeof(InspectionEditor.ReportPreviewWindow).GetField("_status", flags)!.GetValue(window)!;
            string temp = (string)typeof(InspectionEditor.ReportPreviewWindow).GetField("_tempFolder", flags)!.GetValue(window)!;
            window.Show();
            var frame = new System.Windows.Threading.DispatcherFrame();
            var started = DateTime.UtcNow;
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            timer.Tick += (_, _) =>
            {
                if (status.Text.EndsWith("pages", StringComparison.Ordinal) || status.Text.EndsWith("page", StringComparison.Ordinal) || DateTime.UtcNow - started > TimeSpan.FromSeconds(60))
                {
                    timer.Stop();
                    frame.Continue = false;
                }
            };
            timer.Start();
            System.Windows.Threading.Dispatcher.PushFrame(frame);
            bool existed = Directory.Exists(temp) && Directory.GetFiles(temp, "*.pdf").Length > 0;
            int count = pages.Children.Count;
            string text = status.Text;
            window.Close();
            result = (count, existed, !Directory.Exists(temp), text);
            System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        InspectFormatLog.Writer = InspectLogWriter.Default;
        return result;
    }

    public static int Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "red-f022-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            ReportFonts.Install();
            Console.WriteLine($"Fonts: Century Gothic {(ReportFonts.IsInstalled("Century Gothic") ? "installed" : "missing, Arial used")}");

            // ---------- A-022-01 Print gate ----------
            string print = File.ReadAllText(Path.Combine("..", "..", "MainWindow.Print.cs"));
            int guard = print.IndexOf("SubmitService.OpenAttachmentMessage", StringComparison.Ordinal);
            int save = print.IndexOf("SaveForSubmit()", StringComparison.Ordinal);
            int gate = print.IndexOf("PassesCompletionGate(InspectionValidator.PrintRefused", StringComparison.Ordinal);
            int open = print.IndexOf("new ReportPreviewWindow(", StringComparison.Ordinal);
            Check("A-022-01", InspectionValidator.PrintRefused == "Cannot print inspection. You need to fix one or more problems with the inspection." &&
                              guard > 0 && guard < save && save < gate && gate < open,
                "Print refuses with INSPECT's text before any preview opens; order: open-attachment guard, save, completion check, preview");

            // ---------- A-022-02 Dispatch ----------
            var kinds = new[] { 44, 67, 106, 129, 97, 27 }.Select(f => ReportBuilder.Dispatch(f, "Example City", 3)).ToList();
            var truck60 = Doc(27, Section("4", "Pour", Rows(60, i => Item($"4.{i}", $"T{i}", i.ToString()))), Section("5", "Other", new[] { Item("5.1", "Placement Diagram", "", pictures: 1, picture: Green) }));
            var truckBuilt = ReportBuilder.Build((JObject)truck60.DeepClone(), AllOn);
            Check("A-022-02", kinds[0].Kind == ReportKind.SpecialConsult && kinds[1] == (ReportKind.Nho, NhoVariant.Plain, false) &&
                              kinds[2].Kind == ReportKind.Default && kinds[3] == (ReportKind.Nho, NhoVariant.Form129, false) &&
                              kinds[4] == (ReportKind.Nho, NhoVariant.Form97, false) && kinds[5].Kind == ReportKind.Default &&
                              truckBuilt.Sections.Any(s => s.Band == SpecialBand.Truck && s.BandPrints),
                "44 Special Consult, 67 NHO, 106 Default, 129 NHO (Buyerwalk), 97 NHO (Superintendent Action Items), 27 Default with truck table");
            bool allRender = new[] { 44, 67, 106, 129, 97, 27 }.All(f =>
            {
                try { return Render(Doc(f, Section("2", "Main", new[] { Item("2.1", "Kitchen", "Fail", "x") }))).Pdf.Length > 0; }
                catch (Exception ex) { Console.WriteLine($"   FormId {f}: {ex.GetType().Name} {ex.Message}"); return false; }
            });
            Check("A-022-02b", allRender, "each of the six FormIds renders a PDF");

            // ---------- A-022-03 Defects only ----------
            var defects = Doc(106, Section("1", "Main", new[]
            {
                Item("1.1", "Alpha", "Pass"), Item("1.2", "Bravo", "Fail"), Item("1.3", "Charlie", "Pass", alwaysPrint: true),
                Item("1.4", "Comments", "some text", control: "Text"), Item("1.5", "Echo", "Pass")
            }));
            string defectText = AllText(Render(defects, new ReportOptions(true, true, true)).Pdf);
            Check("A-022-03", defectText.Contains("Bravo") && defectText.Contains("Charlie") && defectText.Contains("Comments") &&
                              !defectText.Contains("Alpha") && !defectText.Contains("Echo"), "Only Show Defects lists B, C, D and not A or E");

            // ---------- A-022-04 Value text ----------
            var values = Doc(106, Section("1", "Main", new[]
            {
                Item("1.1", "Walk check", "NA", control: "BWCheck"), Item("1.2", "Not inspected", "NI"), Item("1.3", "Blank value", ""),
                Item("1.4", "Ceiling insulation", "R-38", control: "Text"), Item("1.5", "Failed one", "Fail", "x")
            }));
            byte[] valuesPdf = Render(values).Pdf;
            var valueChars = Chars(valuesPdf, 0);
            string Row(string name)
            {
                var label = Find(valuesPdf, 0, name);
                if (label == null) return "<missing>";
                double top = label[0].Box.Top, bottom = label[0].Box.Bottom;
                return new string(valueChars.Where(c => c.Box.Left > 300 && c.Box.Top < bottom && c.Box.Bottom > top).Select(c => c.Char).ToArray()).Trim();
            }
            var failChars = Find(valuesPdf, 0, "Fail - x");
            bool failColoured = failChars != null && Coloured(valuesPdf, 0, failChars, 241, 90, 64);
            Check("A-022-04", Row("Walk check") == "NA" && Row("Not inspected") == "NI" && Row("Blank value") == "" && Row("Ceiling insulation") == "R-38" &&
                              Row("Failed one") == "Fail-x" && Text(valuesPdf, 0).Contains("Fail - x") && failColoured,
                $"value cells NA, NI, empty, R-38; \"Fail - x\" in rgb(241, 90, 64) (got {Row("Walk check")}|{Row("Not inspected")}|{Row("Blank value")}|{Row("Ceiling insulation")}|{Row("Failed one")}, coloured {failColoured})");

            // F-004 V1 through the save projection the Print button uses.
            string insPath = Path.Combine(root, "MyList", "1234567-CPP-1-PQE.ins");
            Directory.CreateDirectory(Path.GetDirectoryName(insPath)!);
            var stored = Doc(106, Section("1", "Main", new[] { Item("1.1", "Legacy NA item", "NA") }));
            stored["InspectionCode"] = "CPP"; stored["Attempt"] = 1; stored["Events"] = new JArray();
            ((JObject)stored["Sections"]![0]!)["SectionId"] = 1;
            ((JObject)stored["Sections"]![0]!)["ControlName"] = "SectionStandardControl";
            ((JObject)stored["Sections"]![0]!["Items"]![0]!)["ItemId"] = 101;
            File.WriteAllText(insPath, stored.ToString(Formatting.None), new UTF8Encoding(false));
            var saver = new SurgicalSaveService();
            var model = saver.Load(insPath);
            var projected = ReportService.ParseDocument(saver.ProjectCurrent(model).ToUtf8Bytes());
            var projectedReport = ReportBuilder.Build(projected, AllOn);
            Check("A-022-04b", projectedReport.Sections[0].Items[0].Value == "NI", "a PassFailNaNi item stored NA prints \"NI\" (F-004 V1, via the save projection)");

            // ---------- A-022-05 Pictures off removes signatures ----------
            var bwt = Doc(160, Section("1", "Walk", new[] { Item("1.1", "Kitchen", "Discussed") }), Section("3", "Sign", new[] { Item("3.1", "Customer signature", "", pictures: 1, picture: Green) }));
            var bwtOn = ReportBuilder.Build((JObject)bwt.DeepClone(), AllOn);
            var bwtOff = ReportBuilder.Build((JObject)bwt.DeepClone(), new ReportOptions(false, false, true));
            byte[] bwtOffPdf = Render(bwt, new ReportOptions(false, false, true)).Pdf;
            bool greenOnAnyPage = Enumerable.Range(0, Pages(bwtOffPdf)).Any(p => ColourBoxes(Raster0(bwtOffPdf, p), 0, 160, 0).Count > 0);
            byte[] bwtOnPdf = Render(bwt).Pdf;
            bool greenWhenOn = Enumerable.Range(0, Pages(bwtOnPdf)).Any(p => ColourBoxes(Raster0(bwtOnPdf, p), 0, 160, 0).Count > 0);
            Check("A-022-05", bwtOn.CustomerSignature != null && bwtOff.CustomerSignature == null && !greenOnAnyPage && greenWhenOn,
                "FormId 160: the customer signature prints with Show Pictures on and not with it off");

            // ---------- A-022-06 Truck table count rule ----------
            byte[] truckPdf = Render(truck60).Pdf;
            string truckText = AllText(truckPdf);
            var truck61 = Doc(27, Section("4", "Pour Log", Rows(61, i => Item($"4.{i}", $"T{i}", i.ToString()))), Section("5", "Other", new[] { Item("5.1", "Placement Diagram", "", pictures: 1, picture: Green) }));
            var built61 = ReportBuilder.Build((JObject)truck61.DeepClone(), AllOn);
            byte[] truck61Pdf = Render(truck61).Pdf;
            string truck61Text = AllText(truck61Pdf);
            bool diagramAsPicture = Enumerable.Range(0, Pages(truck61Pdf)).Any(p => ColourBoxes(Raster0(truck61Pdf, p), 0, 160, 0).Count > 0);
            Check("A-022-06", truckText.Contains("Truck 15") && truckText.Contains("Placement Diagram") && truckBuilt.PlacementDiagram != null &&
                              !truckBuilt.Sections.Single(s => s.Number == "5").Items.Any(i => i.Name == "Placement Diagram"),
                "60 section-4 items: the truck table and the Placement Diagram print (the diagram leaves section 5)");
            Check("A-022-06b", !truck61Text.Contains("Truck 1") && !truck61Text.Contains("Pour Log") && !truck61Text.Contains("T61") && built61.PlacementDiagram == null &&
                               truck61Text.Contains("Placement Diagram") && diagramAsPicture,
                "61 items: no section 4 at all; the Placement Diagram prints as an ordinary item picture in section 5");

            // ---------- A-022-07 Attachments appended / A-022-12 footer ----------
            var many = Doc(106, Section("1", "Main", Rows(90)));
            many["Attachments"] = new JArray(
                Attachment("A.pdf", Plan((300, 300)), referenceOnly: true),
                Attachment("B.pdf", Plan(Enumerable.Range(0, 12).Select(i => (400.0 + i, 500.0)).ToArray()), canExclude: true, annotated: new[] { 5, 9 }),
                Attachment("C.pdf", Plan((350, 450), (351, 450))));
            var manyRendered = Render(many);
            byte[] manyPdf = manyRendered.Pdf;
            Debug(manyPdf, 0, "many");
            Debug(manyPdf, 2, "many");
            int total = Pages(manyPdf);
            var tail = Enumerable.Range(manyRendered.ReportPages, total - manyRendered.ReportPages).Select(p => PageSize(manyPdf, p)).ToList();
            Check("A-022-07", tail.Count == 4 && tail[0].W == 405 && tail[1].W == 409 && tail[2].W == 350 && tail[3].W == 351,
                $"after the report: B's pages 6 and 10, then C's 2 pages; A absent (tail widths {string.Join(",", tail.Select(t => t.W))})");

            bool footers = manyRendered.ReportPages == 3;
            for (int p = 0; p < manyRendered.ReportPages; p++)
            {
                var label = Find(manyPdf, p, $"Page {p + 1} of 3");
                var rast = Raster0(manyPdf, p);
                var fine = Raster0(manyPdf, p, 4.0);
                bool rule = Enumerable.Range(735 * 4 - 4, 12).Any(y => Near(fine.At(1200, y), 241, 91, 64, 25));
                bool right = label != null && Math.Abs(label[^1].Box.Right - 576) <= 4;
                bool caption = label != null && Coloured(manyPdf, p, label, 109, 110, 113);
                bool nothingLeft = !Chars(manyPdf, p).Any(c => c.Box.Top > 735 && c.Box.Right < 470 && !char.IsWhiteSpace(c.Char));
                footers &= label != null && rule && right && caption && nothingLeft;
            }
            bool attachmentsBare = Enumerable.Range(manyRendered.ReportPages, total - manyRendered.ReportPages).All(p => !Text(manyPdf, p).Contains("Page"));
            Check("A-022-12", footers && attachmentsBare,
                $"report pages end with the accent rule and right-aligned \"Page n of 3\" in caption colour, nothing left of it; attachment pages bare (report pages {manyRendered.ReportPages})");

            // ---------- A-022-13 Page header ----------
            var header2 = Find(manyPdf, 1, "1234567-CPP-1-PQE");
            var header3 = Find(manyPdf, 2, "1234567-CPP-1-PQE");
            var page1Chars = Chars(manyPdf, 0);
            bool page1NoRunning = Find(manyPdf, 0, "1234567-CPP-1-PQE") is { } p1 && p1[0].Box.Top > 50; // only the 15 pt header line
            Check("A-022-13", page1NoRunning && header2 != null && header2[0].Box.Top < 52 && header2[0].Box.Left < 45 &&
                              header3 != null && header3[0].Box.Top < 52 && header3[0].Box.Left < 45 &&
                              header2[0].Box.Bottom - header2[0].Box.Top < 13,
                "page 1 has no running header; pages 2 and 3 start with the inspection number top left in 9.75 pt");

            // ---------- A-022-11 Page setup ----------
            bool sizes = Enumerable.Range(0, manyRendered.ReportPages).All(p => PageSize(manyPdf, p) == (612, 792));
            var ncSc = new[]
            {
                Render(Doc(67, Section("2", "Kitchen", new[] { Item("2.1", "Kitchen", "Discussed", "loose hinge") }))),
                Render(Doc(44, Section("1", "Main", new[] { Item("1.1", "Kitchen", "Fail", "x", pictures: 2) })))
            };
            bool inside = Enumerable.Range(0, manyRendered.ReportPages).All(p => !InkOutside(Raster0(manyPdf, p), 36, 36, 576, 756)) &&
                          ncSc.All(r => Enumerable.Range(0, r.ReportPages).All(p => PageSize(r.Pdf, p) == (612, 792) && !InkOutside(Raster0(r.Pdf, p), 36, 36, 576, 756)));
            Check("A-022-11", sizes && inside, "every Default, NHO and Special Consult report page is 612 × 792 pt portrait with all ink inside 36 pt margins");

            // ---------- A-022-14 Header values ----------
            byte[] headerPdf = Render(Doc(106, Section("1", "Main", new[] { Item("1.1", "Bravo", "Fail") }))).Pdf;
            string headerText = Text(headerPdf, 0);
            var headerRaster = Raster0(headerPdf, 0);
            Debug(headerPdf, 0, "header");
            if (Debugging) Console.WriteLine($"   status px {headerRaster.At((int)(36 + 510 * 0.72), (int)(36 + 66 * 0.72))}");
            Debug(ncSc[0].Pdf, 1, "nho");
            Debug(ncSc[1].Pdf, 1, "sc");
            // Status box (503.9, 63.3) u → pt: x 36 + 503.9 × 0.72 … ; sample its left part, away from the text.
            int sx = (int)(36 + 510 * 0.72), sy = (int)(36 + 66 * 0.72);
            var title = Find(headerPdf, 0, "Concrete Pre Pour");
            var doc2 = Doc(106, Section("1", "Main", new[] { Item("1.1", "Bravo", "Pass") }));
            doc2["StatusId"] = 2; doc2["NextActionText"] = "";
            byte[] passedPdf = Render(doc2).Pdf;
            string passedText = Text(passedPdf, 0);
            var passedRaster = Raster0(passedPdf, 0);
            Check("A-022-14", title != null && title[0].Box.Bottom - title[0].Box.Top >= 10 && headerText.Contains("Failed") &&
                              Near(headerRaster.At(sx, sy), 241, 91, 64) &&
                              headerText.Contains("Next action: Failed items to be inspected at next inspection") &&
                              !headerText.Contains("Next action: Failed items to be inspected at next inspection.") &&
                              headerText.Contains("Main House") && headerText.Contains("100 Example St, Example City") && headerText.Contains("5/B") &&
                              passedText.Contains("Passed") && Near(passedRaster.At(sx, sy), 123, 204, 143) && !passedText.Contains("Next action"),
                "header: 15 pt name, \"Failed\" on accent, next action verbatim, Main House, address combo, 5/B; StatusId 2 \"Passed\" on green; blank next action prints nothing");

            // ---------- A-022-15 Copies ----------
            byte[] copiesPdf = Render(Doc(106, Section("1", "Main", new[] { Item("1.1", "Outlet", "Pass"), Item("1.1", "Outlet", "Fail") }))).Pdf;
            string copiesText = Text(copiesPdf, 0);
            int outlets = copiesText.Split("Outlet").Length - 1;
            Check("A-022-15", outlets == 1 && copiesText.Contains("Fail"), "the copy's row has empty Number and Name and reads \"Fail\"");

            // ---------- A-022-16 Picture grid ----------
            byte[] gridPdf = Render(Doc(106, Section("1", "Main", new[] { Item("1.1", "Pictured", "Fail", pictures: 3) }))).Pdf;
            var grid = Enumerable.Range(0, Pages(gridPdf)).SelectMany(p => ColourBoxes(Raster0(gridPdf, p), 0, 0, 255).Select(b => (p, b))).ToList();
            bool gridOk = grid.Count == 3 &&
                          grid.All(g => Math.Abs(g.b.X1 - g.b.X0 + 1 - 266.4) <= 2 && Math.Abs(g.b.Y1 - g.b.Y0 + 1 - 149.8) <= 2) &&
                          Math.Abs(grid[0].b.Y0 - grid[1].b.Y0) <= 1 && Math.Abs(grid[1].b.X0 - grid[0].b.X0 - 380 * 0.72) <= 2 &&
                          (grid[2].p > grid[0].p || grid[2].b.Y0 > grid[0].b.Y1) && Math.Abs(grid[2].b.X0 - grid[0].b.X0) <= 1;
            byte[] scPdf = ncSc[1].Pdf;
            var sc = Enumerable.Range(0, Pages(scPdf)).SelectMany(p => ColourBoxes(Raster0(scPdf, p), 0, 0, 255)).ToList();
            // 16:9 fitted into 525.6 × 288: height-bound 288, width 512.
            bool scOk = sc.Count == 2 && sc.All(b => Math.Abs(b.Y1 - b.Y0 + 1 - 288) <= 2 && Math.Abs(b.X1 - b.X0 + 1 - 512) <= 2);
            var withSize = Doc(106, Section("1", "Main", new[] { Item("1.1", "Pictured", "Fail", pictures: 3) }));
            ((JObject)withSize["Sections"]![0]!["Items"]![0]!)["PictureSizeId"] = 3;
            byte[] withSizePdf = Render(withSize).Pdf;
            var grid2 = Enumerable.Range(0, Pages(withSizePdf)).SelectMany(p => ColourBoxes(Raster0(withSizePdf, p), 0, 0, 255)).ToList();
            Check("A-022-16", gridOk && scOk && grid2.SequenceEqual(grid.Select(g => g.b)),
                $"Default: 266.4 × 149.8 pt boxes two across, third below the first; Special Consult one per row 525.6 × 288 pt box; PictureSizeId ignored ({grid.Count} / {sc.Count} pictures)");

            // ---------- A-022-17 No failed items ----------
            string emptyText = AllText(Render(Doc(106, Section("1", "Main", Rows(4))), new ReportOptions(true, true, true)).Pdf);
            Check("A-022-17", emptyText.Contains("No failed items.") && emptyText.Contains("Inspector:") && !emptyText.Contains("Row item"),
                "every item Pass with Only Show Defects: \"No failed items.\" and the signature block");

            // ---------- A-022-18 PrePour Express ----------
            JObject PrePour(int count) => Doc(68, Section("8", "PrePour Express", Rows(count, i => Item($"8.{i}", $"Measure {i}", (100 + i).ToString(), pictures: 1))));
            byte[] pp25 = Render(PrePour(25)).Pdf;
            string pp25Text = AllText(pp25);
            bool pp25Pictures = Enumerable.Range(0, Pages(pp25)).Any(p => ColourBoxes(Raster0(pp25, p), 0, 0, 255).Count > 0);
            byte[] pp24 = Render(PrePour(24)).Pdf;
            string pp24Text = AllText(pp24);
            int pp24Pictures = Enumerable.Range(0, Pages(pp24)).Sum(p => ColourBoxes(Raster0(pp24, p), 0, 0, 255).Count);
            Check("A-022-18", pp25Text.Contains("Exterior Measurements") && pp25Text.Contains("101") && pp25Text.Contains("124") && !pp25Text.Contains("125") &&
                              !pp25Text.Contains("Measure 1") && !pp25Pictures &&
                              pp24Text.Contains("Measure 12") && !pp24Text.Contains("Measure 13") && pp24Pictures == 12,
                $"25 items: table of the first 24 values, no grid, no rows; 24 items: table plus 12 captioned pictures ({pp24Pictures})");

            // ---------- A-022-19 Pre-inspection stamp ----------
            var pre = Doc(106, Section("1", "Main", Rows(90)));
            pre["IsPreinspection"] = true;
            var preRendered = Render(pre);
            bool stampEvery = Enumerable.Range(0, preRendered.ReportPages).All(p => Text(preRendered.Pdf, p).Contains("Pre-Inspection"));
            bool stampNone = Enumerable.Range(0, manyRendered.ReportPages).All(p => !Text(manyPdf, p).Contains("Pre-Inspection"));
            Check("A-022-19", preRendered.ReportPages >= 2 && stampEvery && stampNone, "IsPreinspection true: every report page carries the stamp; false: none");

            // ---------- A-022-20 NHO action items ----------
            byte[] nhoPdf = ncSc[0].Pdf;
            int nhoPages = Pages(nhoPdf);
            string nhoBody = Text(nhoPdf, 0);
            string nhoAction = Text(nhoPdf, nhoPages - 1);
            var tick = Find(nhoPdf, 0, "✔");
            bool tickGreen = tick != null && Coloured(nhoPdf, 0, tick, 0x2e, 0xcc, 0x71);
            var kitchen = Find(nhoPdf, 0, "Kitchen");
            string nhoLine2 = ReportBuilder.Build(Doc(67, Section("2", "Kitchen", new[] { Item("2.1", "Kitchen", "Discussed", "loose hinge") })), AllOn).Sections[0].Items[0].Line2;
            var form97 = Doc(97, Section("2", "Kitchen", new[] { Item("2.1", "Sink", "Fail", "leak") }));
            byte[] f97Pdf = Render(form97).Pdf;
            string f97Last = Text(f97Pdf, Pages(f97Pdf) - 1);
            string f97All = AllText(f97Pdf);
            var cross = Find(f97Pdf, 0, "❌");
            bool crossRed = cross != null && Coloured(f97Pdf, 0, cross, 255, 0, 0);
            Check("A-022-20", nhoBody.Contains("Kitchen") && tickGreen && Squeeze(nhoBody).Contains("Item1:Loosehinge") && nhoLine2 == "Item 1:  Loose hinge" && nhoPages >= 2 &&
                              nhoAction.Contains("Superintendent Action Items") && Squeeze(nhoAction).Contains("ItemsToAddressSubContractorDateCalledDateCompleted") &&
                              nhoAction.Contains("2.1 Kitchen") &&
                              Squeeze(nhoAction).Contains("Item1:Loosehinge") && kitchen != null &&
                              Squeeze(f97Last).Contains("ItemstoAddressPassFailDateCompleted") && !f97All.Contains("TEST INTRO") && crossRed,
                $"FormId 67: Kitchen ✔ green and \"Item 1:  Loose hinge\", action page with the four columns and rows; FormId 97 columns, no intro box, red ❌ (tick {tickGreen}, cross {crossRed})");

            // ---------- A-022-21 Acknowledgement gate ----------
            string Ack(string? account)
            {
                var d = Doc(67, Section("2", "Kitchen", new[] { Item("2.1", "Kitchen", "Discussed", "loose hinge") }));
                d["BuilderAccountId"] = account;
                byte[] pdf = Render(d).Pdf;
                return Text(pdf, Pages(pdf) - 1);
            }
            string ackA = Ack(GroupA), ackOther = Ack(Other), ackNull = Ack(null), ackEast = Ack(DfwEast);
            const string AckText = "The above items have been satisfactorily completed and the home is ready to close & occupy.";
            Check("A-022-21", ackA.Contains(AckText) && ackA.Contains("Yes") && ackA.Contains("No") && !ackOther.Contains(AckText) && !ackNull.Contains(AckText) &&
                              ackEast.Contains("Superintendent Action Items") && ackEast.Contains("Customer:") && !ackEast.Contains("Items To Address"),
                "only the group-A account prints the acknowledgement band; others and null render without it; DFW East shows the header block but no table");

            // ---------- A-022-22 NHO sign blocks ----------
            var signed = Doc(67, Section("2", "Kitchen", new[] { Item("2.1", "Kitchen", "Discussed") }), Section("3", "Sign", new[] { Item("3.1", "Customer signature", "", pictures: 1, picture: Green) }));
            byte[] signedPdf = Render(signed).Pdf;
            string signedText = AllText(signedPdf);
            bool signBar = Enumerable.Range(0, Pages(signedPdf)).Any(p => Find(signedPdf, p, "3Sign") != null);
            bool customerSig = Enumerable.Range(0, Pages(signedPdf)).Any(p => ColourBoxes(Raster0(signedPdf, p), 0, 160, 0).Count > 0);
            JObject F129(bool second) { var d = Doc(129, Section("2", "Kitchen", new[] { Item("2.1", "Kitchen", "Discussed") }), Section("4", "Sign", new[] { Item("4.1", "Customer signature", "", pictures: 1, picture: Green) })); d["IsReinspection"] = second; return d; }
            string first129 = AllText(Render(F129(false)).Pdf), second129 = AllText(Render(F129(true)).Pdf);
            Check("A-022-22", !signBar && signedText.Contains("I met with Inspector One representing Example Builder for this New Home Orientation.") &&
                              customerSig && signedText.Contains("Representative:") && signedText.Contains(PrintDate.ToString("M/d/yyyy", System.Globalization.CultureInfo.InvariantCulture)) &&
                              first129.Contains("Buyer's Signature:") && first129.Contains("Signature") && first129.Contains("4.") &&
                              second129.Contains("ACKNOWLEDGEMENT OF COMPLETION: Any and all Punch List Items noted above are complete.") &&
                              !first129.Contains("ACKNOWLEDGEMENT OF COMPLETION"),
                "plain NHO sign block (legal text with names, customer signature, Representative, print date); FormId 129 first and second walk blocks");

            // ---------- A-022-23 City of Kyle ----------
            JObject Kyle(string city, int status)
            {
                var d = Doc(42, Section("1", "Main", Rows(50)));
                d["City"] = city; d["StatusId"] = status;
                d["Attachments"] = new JArray(Attachment("C.pdf", Plan((350, 450))));
                return d;
            }
            var kyle = Render(Kyle("Kyle", 2));
            string kylePage = kyle.ReportPages == 2 ? Text(kyle.Pdf, 2) : "";
            bool kyleFooters = Find(kyle.Pdf, 0, "Page 1 of 2") != null && Find(kyle.Pdf, 1, "Page 2 of 2") != null;
            bool kyleMargin = kyle.ReportPages == 2 && !InkOutside(Raster0(kyle.Pdf, 2), 36, 36, 576, 792 - 21.6);
            var lower = Render(Kyle("kyle", 2));
            var failed = Render(Kyle("Kyle", 3));
            Check("A-022-23", kylePage.Contains("DUCT LEAKAGE AFFIDAVIT") && kylePage.Contains("TEST RESULT:") && kylePage.Contains("PASS") &&
                              kylePage.Contains("Oct 18, 2026") && kylePage.Contains("Inspector One") && !kylePage.Contains("Page") &&
                              Pages(kyle.Pdf) == 4 && PageSize(kyle.Pdf, 3).W == 350 && kyleFooters && kyleMargin &&
                              Pages(lower.Pdf) == lower.ReportPages + 1 && Pages(failed.Pdf) == failed.ReportPages + 1,
                $"page 3 is the affidavit (PASS, date, inspector) before the attachment, inside a 21.6 pt bottom margin; report pages \"Page n of 2\"; \"kyle\" or StatusId 3 adds none (report pages {kyle.ReportPages})");

            // ---------- A-022-24 Truck table without a placement diagram ----------
            var noDiagram = Doc(27, Section("4", "Pour", Rows(60, i => Item($"4.{i}", $"T{i}", i.ToString()))));
            bool noDiagramOk;
            try { noDiagramOk = AllText(Render(noDiagram).Pdf).Contains("Truck 15"); }
            catch (Exception ex) { Console.WriteLine("   " + ex.Message); noDiagramOk = false; }
            Check("A-022-24", noDiagramOk && ReportBuilder.Build((JObject)noDiagram.DeepClone(), AllOn).PlacementDiagram == null,
                "60 truck items and no \"Placement Diagram\": the table prints with an empty diagram box, no error");

            // ---------- A-022-08 / A-022-25 Email draft ----------
            string subject = ReportService.EmailSubject("100 Example St", "Concrete Pre Pour");
            string body = ReportService.EmailBody("Concrete Pre Pour", "100 Example St", "Inspector One");
            string eml = ReportService.BuildEml(subject, body, null, ReportService.PdfFileName("1234567-CPP-1-PQE"), manyPdf);
            Check("A-022-08", subject == "100 Example St - Concrete Pre Pour Inspection Report" && ReportService.PdfFileName("1234567-CPP-1-PQE") == "1234567-CPP-1-PQE.pdf" &&
                              eml.StartsWith("X-Unsent: 1\r\n", StringComparison.Ordinal) && eml.Contains("Subject: " + subject) &&
                              eml.Contains("filename=\"1234567-CPP-1-PQE.pdf\"") && !eml.Contains("\r\nTo: x"),
                "the draft has the subject, the attachment {InspectionNumber}.pdf and X-Unsent: 1 (an unsent draft; RED never sends)");
            Check("A-022-25", body == "<div style='font-size: 12pt; color: #373737;'>Attached is the Concrete Pre Pour inspection report for 100 Example St.<br /><br />Thank you,<br />Inspector One</div>" &&
                              !ReportService.EmailBody("Concrete Pre Pour", "100 Example St", "  ").Contains("Thank you,"),
                "the HTML body matches INSPECT's; a blank inspector name drops \"Thank you,\"");

            // ---------- A-022-09 Options remembered ----------
            string settingsRoot = Path.Combine(root, "appdata");
            var remembered = new ReportSettings();
            ReportService.Remember(remembered, new ReportOptions(false, true, false), settingsRoot);
            var reloaded = ReportService.InitialOptions(ReportSettings.Load(settingsRoot), new InspectorProfile { ReportViewerAttachments = true });
            var fromInspect = ReportService.InitialOptions(new ReportSettings(), new InspectorProfile { ReportViewerDefectsOnly = true, ReportViewerPictures = false });
            Check("A-022-09", !reloaded.Attachments && reloaded.Pictures && fromInspect.DefectsOnly && !fromInspect.Pictures && fromInspect.Attachments &&
                              ReportService.InitialOptions(new ReportSettings(), null) == new ReportOptions(false, true, true),
                "Show Attachments off is remembered for the next preview; first values come from INSPECT's UserConfig; defaults off/on/on");

            // ---------- A-022-10 Preview does not flatten ----------
            byte[] form = FormPdf();
            var formDoc = Doc(106, Section("1", "Main", new[] { Item("1.1", "Bravo", "Fail") }));
            formDoc["Attachments"] = new JArray(Attachment("form.pdf", form));
            string before = formDoc.ToString(Formatting.None);
            var formRendered = Render(formDoc);
            bool unchanged = formDoc.ToString(Formatting.None) == before;
            using var outDoc = PdfReader.Open(new MemoryStream(formRendered.Pdf), PdfDocumentOpenMode.Import);
            var annots = outDoc.Pages[formRendered.ReportPages].Elements.GetArray("/Annots");
            Check("A-022-10", unchanged && annots != null && annots.Elements.Count == 1 &&
                              Convert.FromBase64String((string)formDoc["Attachments"]![0]!["FileData"]!).AsSpan().SequenceEqual(form),
                "the attachment's FileData is untouched (its AcroForm field stays interactive) and the appended page keeps its widget");

            string? dump = Environment.GetEnvironmentVariable("RED_REPORT_DUMP");
            if (!string.IsNullOrEmpty(dump))
            {
                Directory.CreateDirectory(dump);
                foreach (var (name, pdf) in new[] { ("default", manyPdf), ("nho", ncSc[0].Pdf), ("sc", ncSc[1].Pdf), ("kyle", kyle.Pdf), ("truck", truckPdf), ("prepour", pp24), ("form97", f97Pdf), ("signed", signedPdf), ("grid", gridPdf), ("ack", RenderAck(GroupA)) })
                    File.WriteAllBytes(Path.Combine(dump, name + ".pdf"), pdf);
            }
            byte[] RenderAck(string account) { var d = Doc(129, Section("2", "Kitchen", new[] { Item("2.1", "Kitchen", "Discussed", "loose hinge") }), Section("4", "Sign", new[] { Item("4.1", "Customer signature", "", pictures: 1, picture: Green) })); d["BuilderAccountId"] = account; return Render(d).Pdf; }

            // ---------- X1-X4 Preview window (off-screen): pages render, the temp folder goes on close ----------
            var (previewPages, tempExisted, tempGone, status) = PreviewWindowCheck(many, root);
            Check("X4", previewPages == total && tempExisted && tempGone && status.EndsWith("pages", StringComparison.Ordinal),
                $"the preview window shows all {total} pages ({previewPages}, \"{status}\") and deletes its temporary PDFs when it closes");
            // ---------- Review fixes ----------
            // RL L26: every commented item in an action section gets line 2 and a number; only the flagged values are action items.
            var lineTwo = ReportBuilder.Build(Doc(67, Section("2", "Kitchen", new[] { Item("2.1", "Pantry", "Pass", "scratch on door"), Item("2.2", "Kitchen", "Discussed", "loose hinge") })), AllOn);
            var pantry = lineTwo.Sections[0].Items[0]; var kitchenItem = lineTwo.Sections[0].Items[1];
            Check("N4", pantry.Line2 == "Item 1:  Scratch on door" && !pantry.ActionItem && kitchenItem.Line2 == "Item 2:  Loose hinge" && kitchenItem.ActionItem,
                "a commented Pass item prints \"Item 1:\" in the body but is not an action item; the numbering runs on to \"Item 2:\"");
            // RL L23/N5: the sign block prints in place of the Sign section, so none without one.
            string noSign = AllText(Render(Doc(67, Section("2", "Kitchen", new[] { Item("2.1", "Kitchen", "Discussed") }))).Pdf);
            Check("N5", !noSign.Contains("Representative:") && !noSign.Contains("TEST TEXT T2"), "no Sign section (or one dropped by Only Show Defects): no sign block");
            // LA13: a row taller than a page is split line by line; a long word is broken; nothing leaves the margins.
            var tall = Render(Doc(44, Section("1", "Main", new[] { Item("1.1", "Comment 1", "", string.Join(" ", Enumerable.Range(0, 900).Select(i => "narrative" + i))) })));
            var longWord = Render(Doc(106, Section("1", "Main", new[] { Item("1.1", "Link", "Fail", "See " + new string('x', 160)) })));
            bool tallInside = tall.ReportPages >= 2 && Enumerable.Range(0, tall.ReportPages).All(i => !InkOutside(Raster0(tall.Pdf, i), 36, 36, 576, 756));
            Check("LA13", tallInside && !InkOutside(Raster0(longWord.Pdf, 0), 36, 36, 576, 756) && AllText(tall.Pdf).Contains("narrative899"),
                $"a Special Consult narrative longer than a page flows over {tall.ReportPages} pages inside the margins; a 160-character word is broken inside the margins");
            // LA8: Number and Name are each blank when equal to the previous row's.
            string sameName = Text(Render(Doc(106, Section("1", "Main", new[] { Item("1.1", "Outlet", "Pass"), Item("1.2", "Outlet", "Fail") }))).Pdf, 0);
            Check("LA8", sameName.Split("Outlet").Length - 1 == 1 && sameName.Contains("1.2"), "rows 1.1 Outlet and 1.2 Outlet: the second Name is blank, its Number prints");
            // S2: the diagram is section 2's Items[0].Pictures[0]; section 2 prints no other picture.
            var sketchDoc = Doc(44, Section("2", "Sketch", new[] { Item("2.1", "Sketch", "", pictures: 1, picture: Green), Item("2.2", "Photo", "", pictures: 1) }));
            byte[] sketchPdf = Render(sketchDoc).Pdf;
            bool greenDiagram = Enumerable.Range(0, Pages(sketchPdf)).Any(i => ColourBoxes(Raster0(sketchPdf, i), 0, 160, 0).Count > 0);
            bool bluePicture = Enumerable.Range(0, Pages(sketchPdf)).Any(i => ColourBoxes(Raster0(sketchPdf, i), 0, 0, 255).Count > 0);
            Check("S2", greenDiagram && !bluePicture, "Special Consult: Items[0]'s picture is the diagram and section 2 prints no ordinary picture");
            // Unapproved texts print the placeholder, never INSPECT's literals.
            string pending = AllText(Render(Doc(67, Section("2", "Kitchen", new[] { Item("2.1", "Kitchen", "Discussed") })), settings: new ReportSettings()).Pdf);
            Check("Q-49", pending.Contains(ReportSettings.PendingText) && !pending.Contains("I met with"), "with no approved texts the boxes read \"text pending approval\"");
        }
        catch (Exception ex)
        {
            _fail++;
            Console.WriteLine("FAIL harness  " + ex);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
        Console.WriteLine();
        Console.WriteLine($"{_pass} passed, {_fail} failed");
        return _fail == 0 ? 0 : 1;
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
        using var stream = new MemoryStream();
        document.Save(stream, false);
        return stream.ToArray();
    }
}
