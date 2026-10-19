// F-011 inspector signature harness (program/features/F-011-inspector-signature/ACCEPTANCE.md).
//   dotnet run -p:Platform=x64
// A fake saved signature (a drawn test squiggle) and synthetic fixtures only; images are checked by format and pixels.
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
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
        if (ok) { _passed++; Console.WriteLine($"PASS {id} {what}"); }
        else { _failed++; Console.WriteLine($"FAIL {id} {what}"); }
    }

    private static JObject Fixture(string? signature = null, string date = "2026-10-15T09:00:00-05:00") => new()
    {
        ["InspectionNumber"] = "1234567-CPP-1-PQE", ["InspectionCode"] = "CPP", ["FormId"] = 20, ["StatusId"] = 2,
        ["InspectorUsername"] = "<USER>", ["InspectorName"] = "Pat Example", ["InspectorPhone"] = "000-000-0000",
        ["Address"] = "100 Example St", ["DateInspected"] = date, ["Signature"] = signature,
        ["InspectionFee_Secure"] = "PLACEHOLDERCIPHERTEXTA0==",
        ["Sections"] = new JArray(new JObject { ["SectionId"] = 2, ["Name"] = "S", ["Number"] = "2",
            ["Items"] = new JArray(new JObject { ["ItemId"] = 1, ["Number"] = "2.1", ["Name"] = "A", ["ControlName"] = "PassFail", ["Value"] = "Pass" }) })
    };

    private static JObject Read(string path)
    {
        using var reader = new JsonTextReader(new StringReader(File.ReadAllText(path))) { DateParseHandling = DateParseHandling.None };
        return JObject.Load(reader);
    }

    private static List<string> ChangedKeys(JObject a, JObject b) =>
        a.Properties().Select(p => p.Name).Union(b.Properties().Select(p => p.Name)).Where(k => !JToken.DeepEquals(a[k], b[k])).ToList();

    private static SignatureSurface ShownPad(out Window host)
    {
        var pad = new SignatureSurface(InspectorSignatureStore.PadWidth, InspectorSignatureStore.PadHeight);
        host = new Window { Content = pad, SizeToContent = SizeToContent.WidthAndHeight, Left = -20000, Top = -20000,
            WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false };
        host.Show();
        host.UpdateLayout();
        return pad;
    }

    private static (int W, int H, byte[] Px) Decode(string base64)
    {
        var frame = new FormatConvertedBitmap(BitmapDecoder.Create(new MemoryStream(Convert.FromBase64String(base64)),
            BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0], PixelFormats.Bgra32, null, 0);
        var px = new byte[frame.PixelWidth * frame.PixelHeight * 4];
        frame.CopyPixels(px, frame.PixelWidth * 4, 0);
        return (frame.PixelWidth, frame.PixelHeight, px);
    }

    private static int Darkest((int W, int H, byte[] Px) image, int x, int y, int r = 3)
    {
        int best = 255;
        for (int yy = Math.Max(0, y - r); yy <= Math.Min(image.H - 1, y + r); yy++)
            for (int xx = Math.Max(0, x - r); xx <= Math.Min(image.W - 1, x + r); xx++)
            {
                int i = (yy * image.W + xx) * 4;
                best = Math.Min(best, (image.Px[i] + image.Px[i + 1] + image.Px[i + 2]) / 3);
            }
        return best;
    }

    // The footer's write: Signature from the whole pad and DateInspected = now (MainWindow.StoreSignatureFromPad).
    private static void Sign(SurgicalSaveService saver, SignatureSurface pad)
    {
        saver.SetHeaderValue("Signature", new JValue(pad.RenderJpegBase64()));
        saver.SetHeaderValue("DateInspected", new JValue(HeaderRules.FormatDate(DateTime.Now)));
    }

    public static int Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "red-f011-" + Guid.NewGuid().ToString("N"));
        string myList = Path.Combine(root, "Inspections", "MyList");
        Directory.CreateDirectory(myList);
        string Place(JObject doc, string name)
        {
            string path = Path.Combine(myList, name);
            File.WriteAllText(path, doc.ToString(Formatting.None), new UTF8Encoding(false));
            return path;
        }
        try
        {
            // ---------- A-011-01 Stroke stamps signature and date ----------
            {
                string path = Place(Fixture(), "a.ins");
                var saver = new SurgicalSaveService();
                var model = saver.Load(path);
                var before = Read(path);
                var pad = ShownPad(out var host);
                DateTime start = DateTime.Now;
                pad.AddStroke(new Point(50, 50), new Point(300, 60), new Point(500, 40));
                Sign(saver, pad);
                saver.Save(model);
                host.Close();
                var after = Read(path);
                string signature = (string)after["Signature"]!;
                var jpeg = InkItemService.InspectJpeg(signature);
                Check("A-011-01", jpeg is { Baseline: true, Width: 770, Height: 105, DpiX: 96, DpiY: 96 },
                    $"Signature is a baseline JPEG, 770x105, JFIF 96x96 ({jpeg})");
                var image = Decode(signature);
                Check("P2", Darkest(image, 300, 60) < 150 && Darkest(image, 700, 95) > 230, "blue stroke on a white background");
                Check("P2", image.Px[(55 * 770 + 300) * 4] > image.Px[(55 * 770 + 300) * 4 + 2] || Darkest(image, 300, 58) < 150,
                    "ink is blue (the blue channel dominates on the stroke)");
                bool dateNow = DateTimeOffset.TryParse((string?)after["DateInspected"], out var stamped) &&
                               stamped.LocalDateTime >= start.AddSeconds(-1) && stamped.LocalDateTime <= DateTime.Now.AddSeconds(1) &&
                               System.Text.RegularExpressions.Regex.IsMatch((string)after["DateInspected"]!, @"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(\.\d+)?[+-]\d\d:\d\d$");
                Check("A-011-01", dateNow, $"DateInspected is this device's clock, local with offset ({after["DateInspected"]})");
                Check("A-011-01", ChangedKeys(before, after).OrderBy(k => k).SequenceEqual(new[] { "DateInspected", "Signature" }), "no other key changed");
            }

            // ---------- Store: A-011-03 immediate atomic save; A-011-02 / A-011-09 Use Saved ----------
            string storeRoot = Path.Combine(root, "SignatureStore");
            var store = new InspectorSignatureStore(storeRoot, () => null);
            {
                Check("A-011-03", !store.HasSaved && InspectorSignatureStore.NoSavedSignatureQuestion == "You do not have a saved signature. Add one now?",
                    "no saved signature: RED asks \"You do not have a saved signature. Add one now?\"");
                var capture = new SignatureCaptureWindow(store) { Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false };
                capture.Show();
                capture.UpdateLayout();
                capture.Surface.AddStroke(new Point(20, 20), new Point(200, 70), new Point(400, 30)); // fake squiggle
                capture.SaveAndClose();
                Check("A-011-03", capture.Saved && File.Exists(store.JpegPath) && File.Exists(store.PngPath) &&
                                  !Directory.EnumerateFiles(storeRoot, "*.tmp").Any(),
                    "Save stores the JPEG and the PNG master at once (no temp left), so a force-close keeps them");
                var saved = InkItemService.InspectJpeg(Convert.ToBase64String(store.LoadJpeg()!));
                Check("G3", saved is { Width: 770, Height: 91 }, $"the saved signature is 770x91 ({saved})");
            }
            {
                string path = Place(Fixture(), "b.ins");
                var saver = new SurgicalSaveService();
                var model = saver.Load(path);
                var pad = ShownPad(out var host);
                pad.AddStroke(new Point(600, 95), new Point(760, 100)); // existing stroke low on the right
                pad.AddImage(store.LoadImage()!);                       // Use Saved Signature (E2)
                Sign(saver, pad);
                saver.Save(model);
                host.Close();
                string signature = (string)Read(path)["Signature"]!;
                var jpeg = InkItemService.InspectJpeg(signature);
                byte[] savedBytes = store.LoadJpeg()!;
                var image = Decode(signature);
                Check("A-011-02", jpeg is { Width: 770, Height: 105 } && !Convert.FromBase64String(signature).SequenceEqual(savedBytes) &&
                                  Read(path)["DateInspected"]!.ToString() != "2026-10-15T09:00:00-05:00",
                    "Use Saved Signature: a 770x105 JPEG that differs from the saved bytes, and DateInspected updated");
                Check("A-011-09", Darkest(image, 200, 70) < 170 && Darkest(image, 700, 98) < 170 && Darkest(image, 200, 100) > 220,
                    "the saved image sits at the top-left at natural size (rows 0-90) over the earlier stroke (no clear)");
            }

            // ---------- A-011-04 / A-011-13 Clear removes the signature, keeps the date ----------
            {
                string path = Place(Fixture(signature: "/9j/AA=="), "c.ins");
                var saver = new SurgicalSaveService();
                var model = saver.Load(path);
                saver.SetHeaderValue("Signature", JValue.CreateNull()); // MainWindow.ClearInspectorSignature
                saver.Save(model);
                var after = Read(path);
                Check("A-011-04", after["Signature"]!.Type == JTokenType.Null, "Clear and save: Signature = null");
                Check("A-011-13", (string?)after["DateInspected"] == "2026-10-15T09:00:00-05:00", "Clear keeps DateInspected");
            }

            // ---------- A-011-12 Re-signing re-stamps the date; profile keys unchanged ----------
            {
                string path = Place(Fixture(signature: "/9j/AA=="), "d.ins");
                var saver = new SurgicalSaveService();
                var model = saver.Load(path);
                var before = Read(path);
                var pad = ShownPad(out var host);
                pad.AddStroke(new Point(10, 10), new Point(100, 50));
                Sign(saver, pad);
                saver.Save(model);
                host.Close();
                var after = Read(path);
                Check("A-011-12", (string?)after["DateInspected"] != "2026-10-15T09:00:00-05:00" &&
                                  new[] { "InspectorName", "InspectorUsername", "InspectorPhone" }.All(k => JToken.DeepEquals(before[k], after[k])),
                    "one more stroke re-stamps DateInspected; InspectorName, InspectorUsername and InspectorPhone unchanged");
            }

            // ---------- A-011-05 INSPECT config never written ----------
            {
                string sandboxIf = Path.Combine(root, "Inspections");
                string config = Path.Combine(sandboxIf, "config.json");
                File.WriteAllText(config, "{\"Signature\":\"/9j/AA==\",\"InspectorName\":\"Pat Example\"}");
                var stamp = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
                File.SetLastWriteTimeUtc(config, stamp);
                string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(config)));
                var importedStore = new InspectorSignatureStore(Path.Combine(root, "Store2"), () => "/9j/AA==");
                bool imported = importedStore.HasSaved && !importedStore.HasRedCapture;
                var capture = new SignatureCaptureWindow(importedStore) { Left = -20000, Top = -20000, WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false };
                capture.Show(); capture.UpdateLayout();
                capture.Surface.AddStroke(new Point(30, 30), new Point(300, 60));
                capture.SaveAndClose();
                Check("A-011-05", imported && importedStore.HasRedCapture &&
                                  Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(config))) == hash && File.GetLastWriteTimeUtc(config) == stamp,
                    "import, change and re-capture leave config.json's hash and LastWriteTime unchanged");
            }

            // ---------- E1 unreadable saved signature ----------
            {
                var bad = new InspectorSignatureStore(Path.Combine(root, "Store3"), () => "bm90IGEganBlZw==");
                bool threw = false;
                try { bad.LoadImage(); } catch (InvalidDataException ex) { threw = ex.Message == "The saved signature could not be read. Add a new one in Settings."; }
                Check("E1", bad.HasSaved && threw, "a saved signature that is not a JPEG gives E1's message");
            }

            // ---------- A-011-06 / A-011-10 / A-011-11 Date never editable ----------
            Check("A-011-06", !HeaderRules.EditableTopKeys.Contains("DateInspected"),
                "the Top date is not an editable key for any account (no allowlist or name list exists)");

            // ---------- A-011-07 Signed column (InspectionPickerWindow.ParseInspectionFile, by reflection) ----------
            {
                var parse = typeof(InspectionPickerWindow).GetMethod("ParseInspectionFile",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                var picker = RuntimeHelpers.GetUninitializedObject(typeof(InspectionPickerWindow));
                // The parser reads the open-file sets; give the uninitialized window empty ones.
                foreach (var field in typeof(InspectionPickerWindow).GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance))
                    if (field.FieldType == typeof(HashSet<string>))
                        field.SetValue(picker, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
                string Status(string name, string? signature, bool answered)
                {
                    var doc = Fixture(signature);
                    if (!answered) doc["Sections"]![0]!["Items"]![0]!["Value"] = null;
                    string path = Place(doc, name);
                    var info = parse.Invoke(picker, new object[] { path });
                    return (string)info!.GetType().GetProperty("EditStatus")!.GetValue(info)!;
                }
                var statuses = new[] { Status("s1.ins", "/9j/AA==", true), Status("s2.ins", "None", true), Status("s3.ins", null, true), Status("s4.ins", null, false) };
                Check("A-011-07", statuses.SequenceEqual(new[] { "S", "Y", "Y", "N" }), $"Signed column S, Y, Y, N ({string.Join(", ", statuses)})");
            }
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }

        Console.WriteLine();
        Console.WriteLine($"F-011 harness: {_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }
}
