using Docnet.Core;
using Docnet.Core.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Tesseract;
using UglyToad.PdfPig;
using DrawingImageFormat = System.Drawing.Imaging.ImageFormat;

namespace InspectionEditor.Services
{
    public class FreeReinspectionAlert
    {
        public string InsType { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public bool IsInMyList { get; set; }
        public string? MyListFilePath { get; set; }
    }

    public static class FreeReinspectionChecker
    {
        private const string FailNextText = "Failed items to be inspected at next inspection";

        public static async Task<List<FreeReinspectionAlert>> CheckAsync(
            string loadedFilePath,
            InspectionTypeConfig config,
            string myListFolder,
            string jobsFolder)
        {
            var alerts = new List<FreeReinspectionAlert>();
            if (config.FreeReinspectionTypes.Count == 0) return alerts;

            string fileName = Path.GetFileNameWithoutExtension(loadedFilePath);
            string[] parts = fileName.Split('-');
            if (parts.Length < 2) return alerts;
            string jobId = parts[0];

            string jobInspFolder = Path.Combine(jobsFolder, jobId, "Inspections");
            if (!Directory.Exists(jobInspFolder)) return alerts;

            foreach (string freeType in config.FreeReinspectionTypes)
            {
                try
                {
                    string? latestPdf = FindLatestPdf(jobInspFolder, jobId, freeType);
                    if (latestPdf == null) continue;

                    string ocrText = await ExtractFirstPageTextAsync(latestPdf);
                    if (!ocrText.Contains(FailNextText, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string? companionPath = Directory
                        .GetFiles(myListFolder, $"{jobId}-{freeType}-*.ins",
                                  SearchOption.TopDirectoryOnly)
                        .FirstOrDefault();

                    alerts.Add(new FreeReinspectionAlert
                    {
                        InsType        = freeType,
                        DisplayName    = GetDisplayName(freeType),
                        IsInMyList     = companionPath != null,
                        MyListFilePath = companionPath,
                    });
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"FreeReinspectionChecker error for {freeType}: {ex.Message}");
                }
            }

            return alerts;
        }

        private static string? FindLatestPdf(string folder, string jobId, string insType)
        {
            return Directory
                .GetFiles(folder, $"{jobId}-{insType}-*.pdf", SearchOption.TopDirectoryOnly)
                .Select(f => new { Path = f, Seq = ParseSeq(Path.GetFileNameWithoutExtension(f)) })
                .Where(x => x.Seq >= 0)
                .OrderByDescending(x => x.Seq)
                .Select(x => x.Path)
                .FirstOrDefault();
        }

        private static int ParseSeq(string nameWithoutExt)
        {
            var p = nameWithoutExt.Split('-');
            // Use last segment — safer if jobId ever contains a hyphen
            return p.Length >= 3 && int.TryParse(p[^1], out int seq) ? seq : -1;
        }

        private static async Task<string> ExtractFirstPageTextAsync(string pdfPath)
        {
            return await Task.Run(() =>
            {
                // 1. PdfPig — fast, works for computer-generated PDFs
                try
                {
                    using var doc = PdfDocument.Open(pdfPath);
                    var page = doc.GetPage(1);
                    string text = page.Text;
                    if (text.Trim().Length >= 50) return text;
                }
                catch (Exception ex) { Debug.WriteLine($"FreeReinspectionChecker PdfPig failed: {ex.Message}"); }

                // 2. Tesseract fallback for scanned/image-based PDFs
                string? tessData = EnergyComplianceService.GetTessDataPathPublic();
                if (tessData == null) return "";

                try
                {
                    using var engine = new TesseractEngine(tessData, "eng", EngineMode.Default);
                    using var docReader = DocLib.Instance.GetDocReader(pdfPath, new PageDimensions(3.0));
                    using var pageReader = docReader.GetPageReader(0); // page index 0 = first page
                    byte[] rawBytes = pageReader.GetImage();
                    int w = pageReader.GetPageWidth();
                    int h = pageReader.GetPageHeight();
                    return OcrBytes(rawBytes, w, h, engine);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"FreeReinspectionChecker Tesseract failed: {ex.Message}");
                    return "";
                }
            });
        }

        private static string OcrBytes(byte[] bgraBytes, int width, int height, TesseractEngine engine)
        {
            try
            {
                using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                var data = bmp.LockBits(new Rectangle(0, 0, width, height),
                    ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                Marshal.Copy(bgraBytes, 0, data.Scan0, bgraBytes.Length);
                bmp.UnlockBits(data);
                using var ms = new MemoryStream();
                bmp.Save(ms, DrawingImageFormat.Png);
                using var pix = Pix.LoadFromMemory(ms.ToArray());
                using var pg = engine.Process(pix);
                return pg.GetText() ?? "";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"FreeReinspectionChecker OcrBytes error: {ex.Message}");
                return "";
            }
        }

        private static string GetDisplayName(string insType)
        {
            if (_typeNames.TryGetValue(insType.ToUpperInvariant(), out var n)) return n;
            Debug.WriteLine($"FreeReinspectionChecker: no display name for type '{insType}'");
            return insType;
        }

        private static readonly Dictionary<string, string> _typeNames =
            new(StringComparer.OrdinalIgnoreCase)
        {
            ["AFI"]  = "ACCA 310 Field Inspection",
            ["BC"]   = "Builder Confirmation",
            ["BF"]   = "BMEP Final",
            ["BWT"]  = "New Home Orientation",
            ["COH"]  = "Flashing Sheathing Framing (COH)",
            ["CPP"]  = "Concrete Pre-Pour",
            ["CPR"]  = "Concrete Pour",
            ["FS"]   = "Frame",
            ["FSF"]  = "Flashing Sheathing Framing",
            ["FWI"]  = "Stage Three Fire",
            ["HEF"]  = "Final Energy Star",
            ["HER"]  = "HERS Energy Rough",
            ["HET"]  = "Energy Star Final Testing",
            ["IAP"]  = "Indoor Air Plus",
            ["IEF"]  = "Energy Star Final",
            ["IER"]  = "Energy Star Rough",
            ["ME"]   = "BMEP Rough",
            ["MP"]   = "BMEP Rough",
            ["PLY"]  = "Polyseal",
            ["PPE"]  = "Concrete Post Pour Elevations",
            ["QIER"] = "Energy Rough",
            ["SCI"]  = "Special Consult",
            ["SRP"]  = "Slab Repair Pre-Pour",
            ["STR"]  = "Stressing",
            ["SWI"]  = "Shearwall Inspection",
            ["TFF"]  = "TDI Final Frame",
            ["TPC"]  = "TDI Pre Cornice",
            ["TRDI"] = "TDI Roof Decking Inspection",
            ["TRSI"] = "TDI Roof Shingle Inspection",
        };
    }
}
