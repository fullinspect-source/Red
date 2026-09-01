using Docnet.Core;
using Docnet.Core.Models;
using InspectionEditor.Models;
using Newtonsoft.Json.Linq;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UglyToad.PdfPig;

namespace InspectionEditor.Services
{
    public static class PlanCheckService
    {
        private sealed record CheckDefinition(string Id, string Label, string Description, string[] Keywords);

        private static readonly CheckDefinition[] Definitions =
        {
            new("steel", "Steel / rebar callouts on footprint", "rebar, reinforcing steel, bars", new[] { "rebar", "reinforcing", "reinforcement", "steel", "#4", "#5" }),
            new("beam", "Beam design width / depth", "beam width and depth", new[] { "beam", "grade beam", "width", "depth" }),
            new("slab", "Design slab thickness", "slab thickness", new[] { "slab", "thickness", "thick", "thk" }),
            new("hold-down", "Wind-strap / hold-down count", "hold-down, holddown, strap, STHD", new[] { "hold-down", "holddown", "hold down", "strap", "sthd" }),
            new("cables", "Cable counts", "cable, strand, tendon count", new[] { "cable", "cables", "strand", "strands", "tendon", "tendons" })
        };

        public static IReadOnlyList<PlanPdfAttachment> GetEmbeddedPdfAttachments(InspectionFile inspection)
        {
            var result = new List<PlanPdfAttachment>();
            foreach (object raw in inspection.Attachments ?? new List<object>())
            {
                JObject? obj = raw as JObject ?? (raw is JToken token ? token as JObject : JObject.FromObject(raw));
                if (obj == null) continue;
                string filename = obj.Value<string>("Filename") ?? obj.Value<string>("EditPath") ?? "Embedded plan.pdf";
                string fileType = obj.Value<string>("FileType") ?? "";
                string? encoded = obj.Value<string>("FileData");
                if (string.IsNullOrWhiteSpace(encoded)) continue;
                if (!filename.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) &&
                    !fileType.Contains("pdf", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    int comma = encoded.IndexOf(',');
                    if (encoded.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma >= 0)
                        encoded = encoded[(comma + 1)..];
                    byte[] bytes = Convert.FromBase64String(encoded);
                    if (bytes.Length >= 5 && bytes[0] == (byte)'%' && bytes[1] == (byte)'P' && bytes[2] == (byte)'D' && bytes[3] == (byte)'F')
                        result.Add(new PlanPdfAttachment { Source = obj, PdfBytes = bytes, Filename = Path.GetFileName(filename) });
                }
                catch (FormatException) { }
            }
            return result;
        }

        public static List<PlanCheckFinding> CreateFindings(string pdfPath)
        {
            // Spread fallback markers across the first sheet so raster/flattened plans do not
            // stack all five checks on one unreadable point. Searchable text replaces these.
            var fallback = new Dictionary<string, (double X, double Y)>
            {
                ["steel"] = (0.30, 0.38),
                ["beam"] = (0.55, 0.38),
                ["slab"] = (0.72, 0.25),
                ["hold-down"] = (0.30, 0.68),
                ["cables"] = (0.68, 0.68)
            };
            var findings = Definitions.Select(d => new PlanCheckFinding
            {
                Id = d.Id,
                Label = d.Label,
                SearchDescription = d.Description,
                PageIndex = 0,
                X = fallback[d.Id].X,
                Y = fallback[d.Id].Y,
                SuggestionText = "Raster-plan starting point only — tap the exact callout to reposition before marking green or red."
            }).ToList();

            try
            {
                using var document = UglyToad.PdfPig.PdfDocument.Open(pdfPath);
                foreach (var page in document.GetPages())
                {
                    var words = page.GetWords().ToList();
                    foreach (var definition in Definitions)
                    {
                        var finding = findings.First(f => f.Id == definition.Id);
                        if (finding.IsSuggested) continue;
                        var match = words.FirstOrDefault(w => definition.Keywords.Any(k =>
                            w.Text.Contains(k, StringComparison.OrdinalIgnoreCase)));
                        if (match == null) continue;
                        double width = Math.Max(1, page.Width);
                        double height = Math.Max(1, page.Height);
                        finding.PageIndex = Math.Max(0, page.Number - 1);
                        finding.X = Math.Clamp((match.BoundingBox.Left + match.BoundingBox.Right) / 2.0 / width, 0.02, 0.98);
                        finding.Y = Math.Clamp(1.0 - ((match.BoundingBox.Bottom + match.BoundingBox.Top) / 2.0 / height), 0.02, 0.98);
                        finding.IsSuggested = true;
                        finding.SuggestionText = $"Suggested from plan text “{match.Text}” on page {page.Number}. Verify and reposition if needed.";
                    }
                }
            }
            catch (Exception ex)
            {
                foreach (var finding in findings)
                    finding.SuggestionText = $"Text pre-location unavailable ({ex.Message}). Place marker manually.";
            }
            return findings;
        }

        public static (byte[] Bytes, int Width, int Height) RenderPage(string pdfPath, int pageIndex, double scale)
        {
            using var reader = DocLib.Instance.GetDocReader(pdfPath, new PageDimensions(Math.Clamp(scale, 0.25, 5.0)));
            if (pageIndex < 0 || pageIndex >= reader.GetPageCount()) throw new ArgumentOutOfRangeException(nameof(pageIndex));
            using var page = reader.GetPageReader(pageIndex);
            byte[] bytes = page.GetImage();
            // PDFium often emits transparent page backgrounds. Composite BGRA over white so
            // WPF and exported crops show black plan linework on a white sheet.
            for (int i = 0; i + 3 < bytes.Length; i += 4)
            {
                int alpha = bytes[i + 3];
                bytes[i] = (byte)((bytes[i] * alpha + 255 * (255 - alpha)) / 255);
                bytes[i + 1] = (byte)((bytes[i + 1] * alpha + 255 * (255 - alpha)) / 255);
                bytes[i + 2] = (byte)((bytes[i + 2] * alpha + 255 * (255 - alpha)) / 255);
                bytes[i + 3] = 255;
            }
            return (bytes, page.GetPageWidth(), page.GetPageHeight());
        }

        public static int GetPageCount(string pdfPath)
        {
            using var reader = DocLib.Instance.GetDocReader(pdfPath, new PageDimensions(0.25));
            return reader.GetPageCount();
        }

        public static byte[] CreateDeficiencyCrop(string pdfPath, PlanCheckFinding finding)
        {
            var rendered = RenderPage(pdfPath, finding.PageIndex, 2.5);
            using Image<Bgra32> image = Image.LoadPixelData<Bgra32>(rendered.Bytes, rendered.Width, rendered.Height);
            // PDFium can return a transparent page background with black drawing data in the
            // alpha channel. Composite every pixel over white before saving a report image.
            image.ProcessPixelRows(accessor =>
            {
                for (int y = 0; y < image.Height; y++)
                {
                    Span<Bgra32> row = accessor.GetRowSpan(y);
                    for (int x = 0; x < row.Length; x++)
                    {
                        Bgra32 pixel = row[x];
                        int alpha = pixel.A;
                        pixel.R = (byte)((pixel.R * alpha + 255 * (255 - alpha)) / 255);
                        pixel.G = (byte)((pixel.G * alpha + 255 * (255 - alpha)) / 255);
                        pixel.B = (byte)((pixel.B * alpha + 255 * (255 - alpha)) / 255);
                        pixel.A = 255;
                        row[x] = pixel;
                    }
                }
            });
            int centerX = (int)(finding.X * rendered.Width);
            int centerY = (int)(finding.Y * rendered.Height);
            int cropWidth = Math.Min(rendered.Width, Math.Max(700, rendered.Width / 3));
            int cropHeight = Math.Min(rendered.Height, Math.Max(500, rendered.Height / 3));
            int left = Math.Clamp(centerX - cropWidth / 2, 0, rendered.Width - cropWidth);
            int top = Math.Clamp(centerY - cropHeight / 2, 0, rendered.Height - cropHeight);
            image.Mutate(x => x.Crop(new SixLabors.ImageSharp.Rectangle(left, top, cropWidth, cropHeight)));

            // Burn a red target into the crop so the exported evidence preserves the exact
            // inspector-selected plan location, not merely the surrounding plan context.
            int targetX = centerX - left;
            int targetY = centerY - top;
            var red = new Bgra32(255, 0, 0, 255);
            image.ProcessPixelRows(accessor =>
            {
                const int radius = 28;
                const int thickness = 5;
                for (int y = Math.Max(0, targetY - radius); y <= Math.Min(image.Height - 1, targetY + radius); y++)
                {
                    Span<Bgra32> row = accessor.GetRowSpan(y);
                    for (int x = Math.Max(0, targetX - radius); x <= Math.Min(image.Width - 1, targetX + radius); x++)
                    {
                        double distance = Math.Sqrt((x - targetX) * (x - targetX) + (y - targetY) * (y - targetY));
                        if (distance >= radius - thickness && distance <= radius)
                            row[x] = red;
                    }
                }
            });
            using var output = new MemoryStream();
            image.Save(output, new PngEncoder());
            return output.ToArray();
        }

        public static byte[] CreateAnnotatedPdf(string sourcePdfPath, IReadOnlyList<PlanCheckFinding> findings)
        {
            using var input = PdfReader.Open(sourcePdfPath, PdfDocumentOpenMode.Import);
            using var output = new PdfSharp.Pdf.PdfDocument();
            for (int i = 0; i < input.PageCount; i++)
            {
                PdfPage page = output.AddPage(input.Pages[i]);
                using var graphics = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);
                foreach (var finding in findings.Where(f => f.PageIndex == i))
                {
                    XColor color = finding.State == PlanCheckState.Confirmed ? XColors.ForestGreen :
                        finding.State == PlanCheckState.Deficient ? XColors.Red : XColors.Gray;
                    double x = finding.X * page.Width.Point;
                    double y = finding.Y * page.Height.Point;
                    var pen = new XPen(color, 3);
                    graphics.DrawEllipse(pen, x - 10, y - 10, 20, 20);
                    graphics.DrawEllipse(pen, x - 14, y - 14, 28, 28);
                }
            }
            using var stream = new MemoryStream();
            output.Save(stream, false);
            return stream.ToArray();
        }

        public static JObject CreateAnnotatedAttachment(PlanPdfAttachment original, byte[] annotatedPdf)
        {
            var attachment = (JObject)original.Source.DeepClone();
            string stem = Path.GetFileNameWithoutExtension(original.Filename);
            string filename = $"{stem} - RED Plan Check.pdf";
            attachment["Id"] = Guid.NewGuid().ToString();
            attachment["EditPath"] = filename;
            attachment["Filename"] = filename;
            attachment["FileData"] = Convert.ToBase64String(annotatedPdf);
            attachment["ReducedFileData"] = null;
            if (attachment["FileType"] == null)
                attachment["FileType"] = "application/pdf";
            attachment["AnnotatedPages"] = new JArray();
            attachment["IncludedPages"] = new JArray();
            attachment["ReferenceOnly"] = false;
            attachment["ServerPath"] = null;
            return attachment;
        }
    }
}
