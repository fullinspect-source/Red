using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using InspectionEditor.Models;

namespace InspectionEditor.Services
{
    public enum InkControlKind { None, Signature, Sketch, FoundationSketch }

    // F-006: the three INSPECT ink item controls (customer Signature item, Sketch, FoundationSketch). Each stores the
    // whole canvas as one JPEG picture on the item and sets Value "ok" (SPEC S1-S7).
    public static class InkItemService
    {
        public const string ClearSketchQuestion = "Are you sure you clear the sketch?";
        public const string NoFfpMessage = "No foundation footprint (FFP) PDF in the job folder.";
        public const string InkValue = "ok";

        // Observed canvas sizes (SPEC S3, DOC §18): Signature item 770x91, FoundationSketch 772x578. INSPECT's Sketch
        // shares SketchControl with FoundationSketch, so RED uses 772x578 for it too (its own size is not yet observed).
        public static (int Width, int Height) CanvasSize(InkControlKind kind) =>
            kind == InkControlKind.Signature ? (770, 91) : (772, 578);

        public static readonly string[] PenColorNames = { "Black", "Red", "Green", "Blue" };

        public static InkControlKind KindOf(Item? item) => item?.ControlName switch
        {
            "Signature" => InkControlKind.Signature,
            "Sketch" => InkControlKind.Sketch,
            "FoundationSketch" => InkControlKind.FoundationSketch,
            _ => InkControlKind.None
        };

        public static bool IsInk(Item? item) => KindOf(item) != InkControlKind.None;

        // S2: Caption = Item.Label = Number + " " + Name, without the asterisk.
        public static string Label(Item item) => $"{item.Number} {item.Name}";

        public static bool HasInkPicture(Item item) => item.Pictures?.Any(p => p != null && !string.IsNullOrEmpty(p.Data)) == true;

        // S7: an ink item without a picture shows empty, whatever its Value says. Nothing is written on open.
        public static bool ShowsEmpty(Item item) => !HasInkPicture(item);

        // Validation (SPEC §6): a required ink item needs Value "ok" and a picture.
        public static bool IsMissing(Item item) => !HasInkPicture(item) || !string.Equals(item.Value?.ToString(), InkValue, StringComparison.Ordinal);

        // S1: Pictures becomes exactly one INSPECT picture (Path null, Caption label, Description null, Image JPEG) and
        // Value "ok". A new Picture object, so F-002's writer emits INSPECT's four keys and drops any old ones (C1).
        public static void ApplyInk(Item item, string base64Jpeg)
        {
            item.Pictures = new List<Picture>
            {
                new Picture { Title = Label(item), Comment = null, Data = base64Jpeg, SortOrder = 0 }
            };
            item.Value = InkValue;
        }

        // S4: Clear empties Pictures and sets Value to null.
        public static void ClearInk(Item item)
        {
            item.Pictures = new List<Picture>();
            item.Value = null;
        }

        // §5: page 1 of {IF}\Jobs\{job7}\Engineering\{job7}FFP.pdf, job7 = the first 7 characters of JobNumber.
        public static string? FfpPath(string? inspectionsFolder, string? jobNumber)
        {
            if (string.IsNullOrWhiteSpace(inspectionsFolder) || string.IsNullOrWhiteSpace(jobNumber))
                return null;
            string job = jobNumber.Trim();
            string job7 = job.Length > 7 ? job.Substring(0, 7) : job;
            if (job7.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return null;
            return Path.Combine(inspectionsFolder, "Jobs", job7, "Engineering", job7 + "FFP.pdf");
        }

        // §4: label stamps 1 to 9, each tap advances, 9 wraps to 1.
        public static int NextLabel(int current) => current >= 9 || current < 1 ? 1 : current + 1;

        // SketchPenColor: Red, Blue, Green; anything else (null included) is Black (InspectConfigurationsManager::GetColor).
        public static string NormalizePenColor(string? name) => name switch
        {
            "Red" => "Red",
            "Blue" => "Blue",
            "Green" => "Green",
            _ => "Black"
        };

        // The last colour is remembered as SketchPenColor, in RED's own profile (never INSPECT's config). Shared by the
        // F-006 drawing window and the F-023 photo editor.
        public static void RememberPenColor(string color)
        {
            try
            {
                var profile = InspectorProfileService.Load();
                if (profile != null && profile.SketchPenColor != color)
                {
                    profile.SketchPenColor = color;
                    InspectorProfileService.Save(profile);
                }
            }
            catch (Exception ex)
            {
                DiagnosticLogService.Log("Could not remember the sketch pen colour", ex);
            }
        }
        public static Color PenColor(string? name) => NormalizePenColor(name) switch
        {
            "Red" => Color.FromRgb(255, 0, 0),
            "Blue" => Color.FromRgb(0, 0, 255),
            "Green" => Color.FromRgb(0, 128, 0),
            _ => Color.FromRgb(0, 0, 0)
        };

        // S3: baseline JPEG, JFIF 96 dpi, encoder default quality (no QualityLevel set, as INSPECT).
        public static string EncodeJpegBase64(BitmapSource bitmap)
        {
            BitmapSource source = bitmap;
            if (bitmap.Format != PixelFormats.Bgr24 && bitmap.Format != PixelFormats.Bgr32)
            {
                // Flatten onto white so transparent pixels never turn black in the JPEG.
                var visual = new DrawingVisual();
                using (var context = visual.RenderOpen())
                {
                    context.DrawRectangle(Brushes.White, null, new System.Windows.Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight));
                    context.DrawImage(bitmap, new System.Windows.Rect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight));
                }
                var flat = new RenderTargetBitmap(bitmap.PixelWidth, bitmap.PixelHeight, 96, 96, PixelFormats.Pbgra32);
                flat.Render(visual);
                source = flat;
            }
            var encoder = new JpegBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return Convert.ToBase64String(stream.ToArray());
        }

        public static BitmapSource? DecodePicture(Item item)
        {
            var data = item.Pictures?.FirstOrDefault(p => p != null && !string.IsNullOrEmpty(p.Data))?.Data;
            if (string.IsNullOrEmpty(data))
                return null;
            try
            {
                var bytes = Convert.FromBase64String(data);
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = new MemoryStream(bytes);
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch
            {
                return null;
            }
        }

        // Reads the JFIF density and size of a stored JPEG (A-006-01 / V1 checks). Returns null when not a JPEG.
        public static (int Width, int Height, double DpiX, double DpiY, bool Baseline)? InspectJpeg(string base64)
        {
            try
            {
                byte[] bytes = Convert.FromBase64String(base64);
                if (bytes.Length < 4 || bytes[0] != 0xFF || bytes[1] != 0xD8)
                    return null;
                bool baseline = false;
                int width = 0, height = 0;
                double dpiX = 0, dpiY = 0;
                int i = 2;
                while (i + 4 <= bytes.Length && bytes[i] == 0xFF)
                {
                    byte marker = bytes[i + 1];
                    int length = (bytes[i + 2] << 8) | bytes[i + 3];
                    if (marker == 0xE0 && length >= 16 && bytes[i + 4] == 'J' && bytes[i + 5] == 'F')
                    {
                        int unit = bytes[i + 11];
                        int x = (bytes[i + 12] << 8) | bytes[i + 13];
                        int y = (bytes[i + 14] << 8) | bytes[i + 15];
                        dpiX = unit == 1 ? x : unit == 2 ? x * 2.54 : 0;
                        dpiY = unit == 1 ? y : unit == 2 ? y * 2.54 : 0;
                    }
                    else if (marker is 0xC0 or 0xC1 or 0xC2)
                    {
                        baseline = marker == 0xC0;
                        height = (bytes[i + 5] << 8) | bytes[i + 6];
                        width = (bytes[i + 7] << 8) | bytes[i + 8];
                        break;
                    }
                    i += 2 + length;
                }
                return (width, height, dpiX, dpiY, baseline);
            }
            catch
            {
                return null;
            }
        }
    }
}
