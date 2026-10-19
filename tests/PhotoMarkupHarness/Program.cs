// F-023 markup editor, import, camera roll and "never reprocess" checks (program/features/F-023-photo-parity/ACCEPTANCE.md
// A-023-01..07, A-023-15). The editor runs on an STA thread without being shown; synthetic images only.
//   dotnet run -p:Platform=x64
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using InspectionEditor;
using InspectionEditor.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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
        if (ok) _passed++; else _failed++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {id}  {what}");
    }

    private static byte[] Jpeg(int width, int height, byte grey = 200)
    {
        var pixels = new byte[width * height * 4];
        for (int i = 0; i < pixels.Length; i += 4) { pixels[i] = grey; pixels[i + 1] = grey; pixels[i + 2] = grey; pixels[i + 3] = 255; }
        var bitmap = BitmapSource.Create(width, height, 72, 72, PixelFormats.Bgra32, null, pixels, width * 4);
        var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static (int W, int H, byte[] Bgra) Decode(byte[] jpeg)
    {
        var frame = BitmapDecoder.Create(new MemoryStream(jpeg), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        var pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
        return (converted.PixelWidth, converted.PixelHeight, pixels);
    }

    private static (int R, int G, int B) At((int W, int H, byte[] Bgra) image, int x, int y)
    {
        int i = (y * image.W + x) * 4;
        return (image.Bgra[i + 2], image.Bgra[i + 1], image.Bgra[i]);
    }

    private static IEnumerable<Point> Line(double x1, double y1, double x2, double y2, int steps = 30) =>
        Enumerable.Range(0, steps + 1).Select(i => new Point(x1 + (x2 - x1) * i / steps, y1 + (y2 - y1) * i / steps));

    private static ImageEditorWindow Editor(byte[] image, Func<string?>? load = null, Action<string>? save = null) =>
        new(image, load ?? (() => null), save ?? (_ => { }));

    public static int Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "red-f023-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            byte[] photo = Jpeg(640, 360);

            // ---------- A-023-01 Four colours remembered ----------
            string? remembered = null;
            var first = Editor(photo, () => remembered, c => remembered = c);
            bool startsRed = first.PenColorName == "Red";
            first.SetPenColor("Green", remember: true);
            var second = Editor(photo, () => remembered, c => remembered = c);
            Check("A-023-01", startsRed && remembered == "Green" && second.PenColorName == "Green" &&
                              ImageEditorWindow.PenColorNames.SequenceEqual(new[] { "Red", "Blue", "Black", "Green" }),
                "the pen starts Red, Green is remembered for the next photo, and Red, Blue, Black, Green are offered");
            first.Close(); second.Close();

            // ---------- A-023-02 Number stamps ----------
            var stamps = Editor(photo);
            stamps.SetTool("Label");
            stamps.StartLabelsAt(8);
            foreach (var p in new[] { new Point(50, 50), new Point(120, 50), new Point(190, 50) }) stamps.PlaceLabel(p);
            Check("A-023-02", stamps.StampNumbers.SequenceEqual(new[] { 8, 9, 1 }) && stamps.NextLabelNumber == 2, "Label at 8, three taps: stamps 8, 9, 1");
            stamps.Close();

            // ---------- A-023-03 Per-action undo and redo ----------
            var history = Editor(photo);
            history.SetTool("Draw");
            history.AddStroke(Line(10, 10, 100, 10));
            history.AddStroke(Line(10, 30, 100, 30));
            history.AddStroke(Line(10, 50, 100, 50));
            history.PlaceLabel(new Point(200, 200));
            history.Undo(); history.Undo(); history.Redo();
            Check("A-023-03", history.StrokeCount == 3 && history.StampNumbers.Count == 0, "S1, S2, S3, T1; Undo twice, Redo once: S1-S3 present, T1 absent");
            history.Close();

            // ---------- Review fixes: stamps keep 30 x 30, no unrecorded erase, no empty undo steps ----------
            var guarded = Editor(photo);
            var canvas0 = (System.Windows.Controls.InkCanvas)guarded.FindName("MarkupCanvas");
            guarded.AddStroke(Line(10, 10, 100, 10));
            var commit = typeof(ImageEditorWindow).GetMethod("Commit", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            var historyField = typeof(ImageEditorWindow).GetField("_history", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            int steps = ((System.Collections.ICollection)historyField.GetValue(guarded)!).Count;
            commit.Invoke(guarded, null); // e.g. Delete with nothing selected
            int stepsAfter = ((System.Collections.ICollection)historyField.GetValue(guarded)!).Count;
            Check("T2", !canvas0.ResizeEnabled && canvas0.EditingModeInverted == System.Windows.Controls.InkCanvasEditingMode.None && steps == 2 && stepsAfter == 2,
                "stamps cannot be resized, the pen end does not erase outside Undo, and an action that changes nothing adds no Undo step");
            guarded.Close();
            // ---------- A-023-04 Caption, description and size kept ----------
            var markup = Editor(photo);
            markup.SetPenColor("Red", remember: false);
            markup.AddStroke(Line(100, 100, 300, 100));
            byte[] flattened = markup.FlattenWithMarkup();
            var flat = Decode(flattened);
            var mark = At(flat, 200, 100);
            markup.Close();
            // Laid out in a large window, the photo is letterboxed and scaled: a mark at the canvas centre lands at the
            // photo's centre in the output.
            var shown = Editor(photo);
            shown.WindowState = WindowState.Normal;
            var content = (FrameworkElement)shown.Content;
            content.Measure(new Size(1200, 1000));
            content.Arrange(new Rect(0, 0, 1200, 1000));
            content.UpdateLayout();
            var canvas = (FrameworkElement)shown.FindName("MarkupCanvas");
            shown.AddStroke(Line(canvas.ActualWidth / 2 - 20, canvas.ActualHeight / 2, canvas.ActualWidth / 2 + 20, canvas.ActualHeight / 2));
            var centred = Decode(shown.FlattenWithMarkup());
            var centre = At(centred, 320, 180);
            shown.Close();
            string mainSource = File.ReadAllText(Path.Combine("..", "..", "MainWindow.xaml.cs"));
            int click = mainSource.IndexOf("private void PhotoImage_Click", StringComparison.Ordinal);
            string clickBody = click < 0 ? "" : mainSource.Substring(click, Math.Min(2500, mainSource.Length - click));
            bool keepsFields = clickBody.Contains("currentPicture.Data = Convert.ToBase64String(editor.EditedImageBytes);") &&
                               !clickBody.Contains("currentPicture.Title =") && !clickBody.Contains("currentPicture.Comment =");
            Check("A-023-04", flat.W == 640 && flat.H == 360 && mark.R > 180 && mark.G < 90 && centred.W == 640 && centred.H == 360 &&
                              centre.R > 150 && centre.G < 120 && keepsFields,
                $"Done keeps Caption and Description (only Data is replaced) and the source pixel size 640x360; marks map onto the photo (mark {mark}, centre {centre})");

            // ---------- A-023-05 Multi-select import ----------
            int select = mainSource.IndexOf("private void SelectPhotoButton_Click", StringComparison.Ordinal);
            string selectBody = select < 0 ? "" : mainSource.Substring(select, Math.Min(4000, mainSource.Length - select));
            Check("A-023-05", selectBody.Contains("Multiselect = true") && selectBody.Contains("foreach (string file in dialog.FileNames)") &&
                              selectBody.Contains("AddPhotoToCurrentItem(imageData);"),
                "the import dialog allows several files and adds one picture per file in the dialog's order (source check; dialog not driven)");

            // ---------- A-023-08 (wiring) Capture and import share the T1 path ----------
            int captured = mainSource.IndexOf("private void OnPhotoCaptured", StringComparison.Ordinal);
            string capturedBody = captured < 0 ? "" : mainSource.Substring(captured, 400);
            Check("A-023-08w", capturedBody.Contains("AddPhotoToCurrentItem(photoData);") &&
                               mainSource.Contains("return PhotoProcessingService.Process(photoData, WpfPhotoFallback.Process);"),
                "camera capture and File import both go through AddPhotoToCurrentItem → PhotoProcessingService.Process (T1)");

            // ---------- A-023-06 Projection drops RED picture keys ----------
            var server = new JObject
            {
                ["FormId"] = 20, ["InspectionNumber"] = "1234567-CPP-1-PQE",
                ["Sections"] = new JArray(new JObject
                {
                    ["SectionId"] = 1, ["Number"] = "1", ["Name"] = "Main",
                    ["Items"] = new JArray(new JObject
                    {
                        ["ItemId"] = 1, ["Number"] = "1.1", ["Name"] = "Wall", ["ControlName"] = "PassFailNaNi", ["Value"] = "Pass",
                        ["Pictures"] = new JArray(new JObject
                        {
                            ["Path"] = null, ["Caption"] = "Picture", ["Description"] = "North wall", ["Image"] = Convert.ToBase64String(photo),
                            ["Filename"] = "1.1_1.jpg", ["SortOrder"] = 0
                        })
                    })
                })
            };
            var projected = JObject.Parse(Encoding.UTF8.GetString(InspectProjectionWriter.Project(server).ToUtf8Bytes()));
            var picture = (JObject)projected["Sections"]![0]!["Items"]![0]!["Pictures"]![0]!;
            Check("A-023-06", picture.Properties().Select(p => p.Name).SequenceEqual(new[] { "Path", "Caption", "Description", "Image" }),
                $"the projected picture has exactly Path, Caption, Description, Image ({string.Join(", ", picture.Properties().Select(p => p.Name))})");

            // ---------- A-023-07 Camera roll resolution ----------
            string pictures = Path.Combine(root, "Known", "Pictures");          // does not exist
            string oneDrive = Path.Combine(root, "OneDrive");
            string roll = Path.Combine(oneDrive, "Pictures", "Camera Roll 2026");
            Directory.CreateDirectory(roll);
            var env = new Dictionary<string, string?> { ["OneDrive"] = oneDrive, ["USERPROFILE"] = Path.Combine(root, "Profile") };
            string? resolved = CameraService.ResolveCameraRoll(v => env.TryGetValue(v, out var x) ? x : null, pictures);
            // The newest Camera Roll* folder of all roots wins; a root with no Camera Roll falls back to the first root.
            string older = Path.Combine(root, "Profile", "Pictures", "Camera Roll");
            Directory.CreateDirectory(older);
            Directory.SetCreationTimeUtc(older, DateTime.UtcNow.AddDays(-30));
            string? withTwo = CameraService.ResolveCameraRoll(v => env.TryGetValue(v, out var x) ? x : null, pictures);
            string bare = Path.Combine(root, "Bare", "Pictures");
            Directory.CreateDirectory(bare);
            string? fallback = CameraService.ResolveCameraRoll(_ => null, bare);
            string? none = CameraService.ResolveCameraRoll(_ => null, Path.Combine(root, "Missing"));
            Check("A-023-07", string.Equals(resolved, roll, StringComparison.OrdinalIgnoreCase) && string.Equals(withTwo, roll, StringComparison.OrdinalIgnoreCase) &&
                              string.Equals(fallback, bare, StringComparison.OrdinalIgnoreCase) && none == null,
                "%OneDrive%\\Pictures\\Camera Roll 2026 resolves (newest Camera Roll* wins; else the first existing root; else none). The watcher on it is not driven here");

            // ---------- A-023-15 Existing pictures are not reprocessed ----------
            string ins = Path.Combine(root, "MyList", "1234567-CPP-1-PQE.ins");
            Directory.CreateDirectory(Path.GetDirectoryName(ins)!);
            string oldRed = Convert.ToBase64String(Jpeg(1984, 1116, 120));
            string inspect = Convert.ToBase64String(Jpeg(1632, 918, 140));
            var stored = new JObject
            {
                ["InspectVersion"] = null, ["WorkOrderId"] = "00000000-0000-0000-0000-000000000023", ["FormId"] = 20, ["InspectionName"] = "Example Inspection",
                ["InspectionNumber"] = "1234567-CPP-1-PQE", ["InspectionCode"] = "CPP", ["Attempt"] = 1, ["StatusId"] = 1, ["JobNumber"] = "1234567",
                ["Address"] = "100 Example St", ["Events"] = new JArray(), ["Attachments"] = new JArray(),
                ["Sections"] = new JArray(new JObject
                {
                    ["SectionId"] = 1, ["Number"] = "1", ["Name"] = "Main", ["ControlName"] = "SectionStandardControl",
                    ["Items"] = new JArray(
                        new JObject
                        {
                            ["ItemId"] = 101, ["Number"] = "1.1", ["Name"] = "Wall", ["ControlName"] = "PassFailNaNi", ["Value"] = "Pass", ["Comments"] = "",
                            ["Pictures"] = new JArray(new JObject { ["Path"] = null, ["Caption"] = "Wall", ["Description"] = "", ["Image"] = oldRed },
                                                      new JObject { ["Path"] = null, ["Caption"] = "Picture", ["Description"] = null, ["Image"] = inspect })
                        },
                        new JObject { ["ItemId"] = 102, ["Number"] = "1.2", ["Name"] = "Other", ["ControlName"] = "Text", ["Value"] = "", ["Comments"] = "" })
                })
            };
            File.WriteAllText(ins, stored.ToString(Formatting.None), new UTF8Encoding(false));
            var saver = new SurgicalSaveService();
            var model = saver.Load(ins);
            model.Sections![0].Items![1].Comments = "unrelated edit";
            saver.Save(model);
            var after = JObject.Parse(File.ReadAllText(ins));
            var images = after["Sections"]![0]!["Items"]![0]!["Pictures"]!.Select(p => (string?)p["Image"]).ToList();
            var cancelled = Editor(Convert.FromBase64String(oldRed));
            bool untouched = !cancelled.ImageWasEdited && cancelled.EditedImageBytes.AsSpan().SequenceEqual(Convert.FromBase64String(oldRed));
            cancelled.Close();
            Check("A-023-15", images.Count == 2 && images[0] == oldRed && images[1] == inspect && (string?)after["Sections"]![0]!["Items"]![1]!["Comments"] == "unrelated edit" && untouched,
                "open, unrelated edit and save leave both pictures' Image bytes unchanged; an editor opened and cancelled returns the same bytes");
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine("FAIL harness  " + ex);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
        Console.WriteLine();
        Console.WriteLine($"{_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }
}
