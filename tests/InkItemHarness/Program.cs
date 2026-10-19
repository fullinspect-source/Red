// F-006 ink item harness (program/features/F-006-ink-items/ACCEPTANCE.md).
//   dotnet run -p:Platform=x64
// Synthetic fixtures only (job 1234567, 100 Example St, a generated sample FFP PDF, synthetic strokes). Images are
// checked by format, size and a few pixel samples; no real signature or sketch is read.
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using InspectionEditor;
using InspectionEditor.Models;
using InspectionEditor.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

int exit = 1;
var thread = new Thread(() => exit = Harness.Run());
thread.SetApartmentState(ApartmentState.STA);
thread.Start();
thread.Join();
return exit;

static class Harness
{
    private static int _passed, _failed;

    private static void Check(string id, bool ok, string what)
    {
        if (ok) { _passed++; Console.WriteLine($"PASS {id} {what}"); }
        else { _failed++; Console.WriteLine($"FAIL {id} {what}"); }
    }

    private static Item NewItem(string number, string name, string control, string? value) =>
        new() { ItemId = 1, Number = number, Name = name, ControlName = control, Value = value, Pictures = new List<Picture>() };

    private static InkItemEditorWindow Open(Item item, string? ffp = null, Func<string?>? load = null, Action<string>? save = null)
    {
        var window = new InkItemEditorWindow(item, ffp, _ => { }, load ?? (() => null), save ?? (_ => { }))
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000,
            Top = -20000,
            ShowActivated = false,
            ShowInTaskbar = false
        };
        window.Show();
        window.UpdateLayout();
        return window;
    }

    private static (int W, int H, byte[] Pixels) Decode(string base64)
    {
        var decoder = BitmapDecoder.Create(new MemoryStream(Convert.FromBase64String(base64)), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Bgra32, null, 0);
        var pixels = new byte[frame.PixelWidth * frame.PixelHeight * 4];
        frame.CopyPixels(pixels, frame.PixelWidth * 4, 0);
        return (frame.PixelWidth, frame.PixelHeight, pixels);
    }

    // Darkest pixel brightness (0-255) in a small box around (x, y).
    private static int Darkest((int W, int H, byte[] Pixels) image, int x, int y, int radius = 3)
    {
        int best = 255;
        for (int yy = Math.Max(0, y - radius); yy <= Math.Min(image.H - 1, y + radius); yy++)
            for (int xx = Math.Max(0, x - radius); xx <= Math.Min(image.W - 1, x + radius); xx++)
            {
                int i = (yy * image.W + xx) * 4;
                best = Math.Min(best, (image.Pixels[i] + image.Pixels[i + 1] + image.Pixels[i + 2]) / 3);
            }
        return best;
    }

    private static IEnumerable<Point> Line(double x1, double y1, double x2, double y2, int steps = 40) =>
        Enumerable.Range(0, steps + 1).Select(i => new Point(x1 + (x2 - x1) * i / steps, y1 + (y2 - y1) * i / steps));

    public static int Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "red-f006-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // ---------- A-006-01 Customer signature stored as one picture ----------
            var signature = NewItem("3.1", "Customer signature", "Signature", null);
            {
                var window = Open(signature);
                window.AddStroke(Line(100, 40, 300, 40));
                window.Close();
                var picture = signature.Pictures.SingleOrDefault();
                var jpeg = picture?.Data != null ? InkItemService.InspectJpeg(picture.Data) : null;
                Check("A-006-01", signature.Pictures.Count == 1 && picture!.Title == "3.1 Customer signature" && picture.Comment == null &&
                                  (string?)signature.Value == "ok",
                    "one picture, Caption \"3.1 Customer signature\", Description null, Value \"ok\"");
                Check("A-006-01", jpeg is { Baseline: true, DpiX: 96, DpiY: 96, Width: 770, Height: 91 } && !picture!.Data!.StartsWith("data:"),
                    $"Image is a baseline JPEG at 96 dpi, 770x91, plain base64 ({jpeg})");
                var image = Decode(picture!.Data!);
                Check("S3", Darkest(image, 200, 40) < 100 && Darkest(image, 600, 80) > 200,
                    "the stroke is in the image on a white background");

                // The saved file carries INSPECT's four picture keys (F-002 writer).
                var doc = new JObject
                {
                    ["InspectionNumber"] = "1234567-BWT-1-XX", ["Address"] = "100 Example St", ["FormId"] = 105,
                    ["Sections"] = new JArray(new JObject
                    {
                        ["SectionId"] = 3, ["Name"] = "Sign", ["ControlName"] = "BWCheck2", ["Number"] = "3",
                        ["Items"] = new JArray(new JObject
                        {
                            ["ItemId"] = 1, ["Name"] = "Customer signature", ["Number"] = "3.1", ["ControlName"] = "Signature",
                            ["Value"] = null, ["Comments"] = null, ["Pictures"] = new JArray(), ["Required"] = true,
                            ["HideCommentsButton"] = true, ["HidePicturesButton"] = true, ["HideAddButton"] = true
                        })
                    })
                };
                string path = Path.Combine(root, "1234567-BWT-1-XX.ins");
                File.WriteAllText(path, doc.ToString(Formatting.None), new UTF8Encoding(false));
                var saver = new SurgicalSaveService();
                var model = saver.Load(path);
                var target = model.Sections[0].Items[0];
                var w2 = Open(target);
                w2.AddStroke(Line(100, 40, 300, 40));
                w2.Close();
                saver.Save(model);
                var saved = JObject.Parse(File.ReadAllText(path));
                var savedItem = (JObject)saved["Sections"]![0]!["Items"]![0]!;
                var savedPicture = (JObject?)((JArray)savedItem["Pictures"]!).SingleOrDefault();
                Check("A-006-01", savedPicture != null &&
                                  savedPicture.Properties().Select(p => p.Name).SequenceEqual(new[] { "Path", "Caption", "Description", "Image" }) &&
                                  savedPicture["Path"]!.Type == JTokenType.Null && (string?)savedPicture["Caption"] == "3.1 Customer signature" &&
                                  savedPicture["Description"]!.Type == JTokenType.Null && (string?)savedItem["Value"] == "ok",
                    "the saved file has exactly { Path null, Caption, Description null, Image } and Value \"ok\"");

                // ---------- A-006-02 Clear empties the item ----------
                model = saver.Load(path);
                target = model.Sections[0].Items[0];
                var w3 = Open(target);
                w3.ClearCanvas(ask: false);
                w3.Close();
                Check("A-006-02", target.Pictures.Count == 0 && target.Value == null, "Clear: Pictures [] and Value null in the model");
                saver.Save(model);
                saved = JObject.Parse(File.ReadAllText(path));
                savedItem = (JObject)saved["Sections"]![0]!["Items"]![0]!;
                Check("A-006-02", ((JArray)savedItem["Pictures"]!).Count == 0 && savedItem["Value"]!.Type == JTokenType.Null,
                    "Clear: the saved file has Pictures [] and Value null");
            }

            // ---------- A-006-03 Strokes accumulate on the existing picture ----------
            {
                var window = Open(signature); // re-open: the pen is blue, the old picture is the background
                window.AddStroke(Line(400, 70, 650, 70));
                window.Close();
                var image = Decode(signature.Pictures.Single().Data!);
                Check("A-006-03", signature.Pictures.Count == 1 && Darkest(image, 200, 40) < 110 && Darkest(image, 520, 70) < 160,
                    "still one picture, holding both the old and the new stroke");
                Check("SPEC 3", window.PenColorName == "Blue", "re-opening an existing signature draws in blue");
            }

            // ---------- A-006-04 Sketch tools ----------
            {
                var sketch = NewItem("2.1", "Sketch", "Sketch", null);
                var window = Open(sketch);
                window.StartLabelsAt(8);
                window.PlaceLabel(new Point(100, 100));
                window.PlaceLabel(new Point(200, 100));
                window.PlaceLabel(new Point(300, 100));
                Check("A-006-04", window.StampNumbers.SequenceEqual(new[] { 8, 9, 1 }), $"stamps read 8, 9, 1 ({string.Join(", ", window.StampNumbers)})");
                window.Undo();
                Check("A-006-04", window.StampNumbers.SequenceEqual(new[] { 8, 9 }), "Undo removes only the last stamp");
                window.Redo();
                Check("A-006-04", window.StampNumbers.SequenceEqual(new[] { 8, 9, 1 }), "Redo restores it");
                var image = Decode(sketch.Pictures.Single().Data!);
                var jpeg = InkItemService.InspectJpeg(sketch.Pictures.Single().Data!);
                Check("S1", Darkest(image, 300, 100, 8) < 128 && jpeg is { Width: 772, Height: 578 } && (string?)sketch.Value == "ok",
                    "the redone stamp is in the stored 772x578 picture");
                window.Undo(); window.Undo(); window.Undo();
                Check("S4", sketch.Pictures.Count == 0 && sketch.Value == null, "undoing every action leaves the item empty");
                window.Close();
                Check("§4", InkItemService.NextLabel(9) == 1 && InkItemService.NextLabel(1) == 2, "label numbers wrap 9 to 1");
            }

            // ---------- A-006-05 Sketch clear prompt (text; the Yes/No box itself is UI) ----------
            Check("A-006-05", InkItemService.ClearSketchQuestion == "Are you sure you clear the sketch?",
                "Clear asks \"Are you sure you clear the sketch?\" (No keeps the strokes: UI, not run here)");

            // ---------- A-006-06 Pen colour remembered ----------
            {
                string? remembered = null;
                var first = Open(NewItem("2.1", "Sketch", "Sketch", null), load: () => remembered, save: c => remembered = c);
                first.SetPenColor("Green", remember: true);
                first.Close();
                var second = Open(NewItem("2.2", "Sketch", "Sketch", null), load: () => remembered, save: c => remembered = c);
                Check("A-006-06", remembered == "Green" && second.PenColorName == "Green", "Green is remembered as SketchPenColor and used by the next Sketch");
                second.Close();
                Check("SET §3", InkItemService.NormalizePenColor(null) == "Black" && InkItemService.NormalizePenColor("Purple") == "Black",
                    "a null or unknown SketchPenColor means Black");
            }

            // ---------- A-006-07 FoundationSketch background ----------
            {
                string inspections = Path.Combine(root, "Inspections");
                string engineering = Path.Combine(inspections, "Jobs", "1234567", "Engineering");
                Directory.CreateDirectory(engineering);
                Directory.CreateDirectory(Path.Combine(inspections, "MyList"));
                string ffp = Path.Combine(engineering, "1234567FFP.pdf");
                using (var pdf = new PdfDocument())
                {
                    var page = pdf.AddPage();
                    using (var gfx = XGraphics.FromPdfPage(page))
                        gfx.DrawRectangle(XBrushes.Black, 0, 0, page.Width.Point, 100);
                    pdf.Save(ffp);
                }
                Check("A-006-07", InkItemService.FfpPath(inspections, "1234567-01") == ffp,
                    "the FFP path is {IF}\\Jobs\\{job7}\\Engineering\\{job7}FFP.pdf");
                var diagram = NewItem("5.2", "Placement Diagram", "FoundationSketch", null);
                var window = Open(diagram, ffp);
                window.AddStroke(Line(100, 400, 600, 400));
                window.Close();
                var image = Decode(diagram.Pictures.Single().Data!);
                Check("A-006-07", Darkest(image, 386, 50) < 60 && Darkest(image, 300, 400) < 120 && Darkest(image, 386, 250) > 200 &&
                                  diagram.Pictures.Single().Title == "5.2 Placement Diagram",
                    "page 1 is the background and the stored JPEG holds both the page and the stroke");
            }

            // ---------- A-006-08 FoundationSketch without FFP ----------
            {
                var diagram = NewItem("5.2", "Placement Diagram", "FoundationSketch", null);
                var window = Open(diagram, Path.Combine(root, "missing", "1234567FFP.pdf"));
                window.AddStroke(Line(100, 300, 600, 300));
                window.Close();
                Check("A-006-08", InkItemService.NoFfpMessage == "No foundation footprint (FFP) PDF in the job folder." &&
                                  diagram.Pictures.Count == 1 && (string?)diagram.Value == "ok",
                    "no FFP: the hint text, and drawing still works");
            }

            // ---------- A-006-10 Picture-less ink item on load ----------
            {
                var doc = new JObject
                {
                    ["InspectionNumber"] = "1234567-SCI-1-XX", ["FormId"] = 228,
                    ["Sections"] = new JArray(new JObject
                    {
                        ["SectionId"] = 2, ["Name"] = "Sketch", ["ControlName"] = null, ["Number"] = "2",
                        ["Items"] = new JArray(
                            new JObject { ["ItemId"] = 1, ["Name"] = "Sketch", ["Number"] = "2.1", ["ControlName"] = "Sketch", ["Value"] = "", ["Pictures"] = new JArray() },
                            new JObject { ["ItemId"] = 2, ["Name"] = "Customer signature", ["Number"] = "2.2", ["ControlName"] = "Signature", ["Value"] = "ok", ["Pictures"] = new JArray() })
                    })
                };
                string path = Path.Combine(root, "1234567-SCI-1-XX.ins");
                File.WriteAllText(path, doc.ToString(Formatting.None), new UTF8Encoding(false));
                byte[] before = File.ReadAllBytes(path);
                var saver = new SurgicalSaveService();
                var model = saver.Load(path);
                bool bothEmpty = model.Sections[0].Items.All(InkItemService.ShowsEmpty) && model.Sections[0].Items.All(InkItemService.IsMissing);
                saver.Save(model);
                Check("A-006-10", bothEmpty && File.ReadAllBytes(path).SequenceEqual(before),
                    "both show empty (and validate as missing); open + close without edits leaves the bytes unchanged");
            }
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine($"F-006 harness: {_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }
}
