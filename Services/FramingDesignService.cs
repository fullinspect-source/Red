using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Docnet.Core;
using Docnet.Core.Models;
using Newtonsoft.Json;
using Tesseract;
using UglyToad.PdfPig;

namespace InspectionEditor.Services
{
    internal static class FramingDesignService
    {
        private const int MaxScanPages = 24;
        private const int MaxOcrCandidatePages = 24;
        private static readonly ConcurrentDictionary<string, FramingDesignInfo> MemoryCache = new(StringComparer.OrdinalIgnoreCase);

        internal static bool SupportsInspectionCode(string? inspectionCode)
        {
            string code = EnergyComplianceService.NormalizeCode(inspectionCode);
            return code is "SWI" or "TFF" or "TPC" or "TRDI" or "TRSI" or "COH" or "FS" or "FSF" or "ME" or "MP";
        }

        internal static FramingDesignInfo GetInfoForInspection(string? insFilePath, string? selectedPlanPdf = null)
        {
            var empty = new FramingDesignInfo();
            if (string.IsNullOrWhiteSpace(insFilePath) || !File.Exists(insFilePath))
            {
                empty.StatusText = "No inspection file loaded.";
                return empty;
            }

            string? pdfPath = !string.IsNullOrWhiteSpace(selectedPlanPdf) && File.Exists(selectedPlanPdf)
                ? selectedPlanPdf
                : FindFramingPlanPdf(insFilePath);
            if (string.IsNullOrWhiteSpace(pdfPath) || !File.Exists(pdfPath))
            {
                empty.StatusText = "No framing engineering PDF found.";
                return empty;
            }

            string cacheKey = BuildCacheKey(pdfPath);
            if (MemoryCache.TryGetValue(cacheKey, out var memoryCached))
                return memoryCached;

            var diskCached = TryLoadDiskCache(pdfPath, cacheKey);
            if (diskCached != null)
            {
                MemoryCache[cacheKey] = diskCached;
                return diskCached;
            }

            FramingDesignInfo info;
            try
            {
                var pages = ExtractPageTexts(pdfPath, out string debugText, out bool extractionComplete);
                info = FramingDesignParser.Parse(pdfPath, pages, extractionComplete);
                info.DebugText = debugText;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Framing extraction failed: {ex.Message}");
                info = new FramingDesignInfo
                {
                    PdfPath = pdfPath,
                    DisplayName = Path.GetFileName(pdfPath),
                    StatusText = "Could not parse framing plan PDF.",
                    DebugText = $"Framing extraction error: {ex.Message}"
                };
            }

            if (info.IsLoaded)
            {
                MemoryCache[cacheKey] = info;
                TrySaveDiskCache(pdfPath, cacheKey, info);
            }
            return info;
        }

        private static List<FramingPageText> ExtractPageTexts(
            string pdfPath,
            out string debugText,
            out bool extractionComplete)
        {
            var nativePages = ReadNativePages(pdfPath);
            int pageCount = nativePages.Count;
            bool mostlyScanned = nativePages.Count == 0 ||
                                 nativePages.Count(page => page.Text.Length >= 500) < Math.Min(2, nativePages.Count);
            string? tessDataPath = EnergyComplianceService.GetTessDataPathPublic();

            var candidateIndexes = new HashSet<int>();
            var readableIndexes = new HashSet<int>();
            for (int i = 0; i < nativePages.Count; i++)
            {
                if (nativePages[i].Text.Length >= 500)
                    readableIndexes.Add(i);
                if (FramingDesignParser.IsCandidateSheetText(nativePages[i].Text))
                    candidateIndexes.Add(i);
                if (nativePages[i].Text.Length < 500 && i < MaxScanPages)
                    candidateIndexes.Add(i);
            }

            if (mostlyScanned || candidateIndexes.Count == 0)
            {
                for (int i = 0; i < Math.Min(pageCount, MaxScanPages); i++)
                    candidateIndexes.Add(i);
            }

            candidateIndexes = candidateIndexes
                .OrderBy(index => index)
                .Take(MaxOcrCandidatePages)
                .ToHashSet();

            int ocrPageCount = 0;
            int windCropCount = 0;
            if (tessDataPath != null && pageCount > 0 && candidateIndexes.Count > 0)
            {
                double scale = mostlyScanned ? 1.6 : 2.8;
                using var engine = new TesseractEngine(tessDataPath, "eng", EngineMode.Default);
                using var docReader = DocLib.Instance.GetDocReader(pdfPath, new PageDimensions(scale));

                foreach (int index in candidateIndexes.OrderBy(index => index))
                {
                    if (index < 0 || index >= docReader.GetPageCount())
                        continue;

                    using var pageReader = docReader.GetPageReader(index);
                    byte[] rawBytes = pageReader.GetImage();
                    int width = pageReader.GetPageWidth();
                    int height = pageReader.GetPageHeight();
                    string ocrText = OcrBytes(rawBytes, width, height, engine, PageSegMode.SparseText);
                    if (!string.IsNullOrWhiteSpace(ocrText))
                    {
                        nativePages[index].Text = string.Join("\n", new[] { nativePages[index].Text, ocrText }
                            .Where(value => !string.IsNullOrWhiteSpace(value)));
                        ocrPageCount++;
                        if (ocrText.Length >= 100)
                            readableIndexes.Add(index);
                    }

                    if (ocrText.Contains("windspeed", StringComparison.OrdinalIgnoreCase))
                    {
                        string cropText = OcrWindDesignCrop(rawBytes, width, height, engine);
                        if (!string.IsNullOrWhiteSpace(cropText))
                        {
                            nativePages[index].Text += "\n" + cropText;
                            windCropCount++;
                        }
                    }
                }
            }

            foreach (var page in nativePages)
            {
                if (string.IsNullOrWhiteSpace(page.SheetName))
                    page.SheetName = FramingDesignParser.DetectSheetName(page.Text);
            }

            extractionComplete = pageCount > 0 && readableIndexes.Count >= pageCount;
            debugText = $"Framing parser v{FramingDesignParser.ParserVersion}; pages={pageCount}; scanned={mostlyScanned}; " +
                        $"candidatePages={candidateIndexes.Count}; ocrPages={ocrPageCount}; windCrops={windCropCount}; " +
                        $"readablePages={readableIndexes.Count}; " +
                        $"complete={extractionComplete}; " +
                        (tessDataPath == null ? "OCR unavailable" : "OCR available");
            return nativePages;
        }

        private static List<FramingPageText> ReadNativePages(string pdfPath)
        {
            var result = new List<FramingPageText>();
            try
            {
                using var document = PdfDocument.Open(pdfPath);
                foreach (var page in document.GetPages())
                {
                    string text = page.Text ?? "";
                    result.Add(new FramingPageText
                    {
                        PageNumber = page.Number,
                        SheetName = FramingDesignParser.DetectSheetName(text),
                        Text = text
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Framing PdfPig extraction failed: {ex.Message}");
            }

            if (result.Count > 0)
                return result;

            try
            {
                using var reader = DocLib.Instance.GetDocReader(pdfPath, new PageDimensions(1.0));
                for (int index = 0; index < reader.GetPageCount(); index++)
                {
                    result.Add(new FramingPageText
                    {
                        PageNumber = index + 1
                    });
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Framing page-count discovery failed: {ex.Message}");
            }

            return result;
        }

        private static string OcrBytes(
            byte[] bgraBytes,
            int width,
            int height,
            TesseractEngine engine,
            PageSegMode pageSegMode)
        {
            try
            {
                using var bitmap = BuildBitmap(bgraBytes, width, height);
                using var stream = new MemoryStream();
                bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                using var pix = Pix.LoadFromMemory(stream.ToArray());
                using var page = engine.Process(pix, pageSegMode);
                return page.GetText() ?? "";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Framing OCR page failed: {ex.Message}");
                return "";
            }
        }

        private static string OcrWindDesignCrop(
            byte[] bgraBytes,
            int width,
            int height,
            TesseractEngine engine)
        {
            try
            {
                Tesseract.Rect? anchorBounds = null;
                using (var bitmap = BuildBitmap(bgraBytes, width, height))
                using (var stream = new MemoryStream())
                {
                    bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png);
                    using var pix = Pix.LoadFromMemory(stream.ToArray());
                    using var page = engine.Process(pix, PageSegMode.SparseText);
                    using var iterator = page.GetIterator();
                    iterator.Begin();
                    do
                    {
                        string line = iterator.GetText(PageIteratorLevel.TextLine) ?? "";
                        if (line.Contains("windspeed", StringComparison.OrdinalIgnoreCase) &&
                            iterator.TryGetBoundingBox(PageIteratorLevel.TextLine, out var bounds))
                        {
                            anchorBounds = bounds;
                            break;
                        }
                    }
                    while (iterator.Next(PageIteratorLevel.TextLine));
                }

                if (!anchorBounds.HasValue)
                    return "";

                var anchor = anchorBounds.Value;
                int left = Math.Max(0, anchor.X1 - (int)(width * 0.18));
                int top = Math.Max(0, anchor.Y1 - (int)(height * 0.035));
                int right = Math.Min(width, anchor.X2 + (int)(width * 0.08));
                int bottom = Math.Min(height, top + (int)(height * 0.20));
                if (right <= left || bottom <= top)
                    return "";

                using var full = BuildBitmap(bgraBytes, width, height);
                using var crop = full.Clone(new Rectangle(left, top, right - left, bottom - top), PixelFormat.Format32bppArgb);
                using var enlarged = new Bitmap(crop, Math.Max(1, crop.Width * 2), Math.Max(1, crop.Height * 2));
                using var cropStream = new MemoryStream();
                enlarged.Save(cropStream, System.Drawing.Imaging.ImageFormat.Png);
                using var cropPix = Pix.LoadFromMemory(cropStream.ToArray());
                using var cropPage = engine.Process(cropPix, PageSegMode.SingleBlock);
                return cropPage.GetText() ?? "";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Framing wind crop OCR failed: {ex.Message}");
                return "";
            }
        }

        private static Bitmap BuildBitmap(byte[] bgraBytes, int width, int height)
        {
            var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            var data = bitmap.LockBits(
                new Rectangle(0, 0, width, height),
                ImageLockMode.WriteOnly,
                PixelFormat.Format32bppArgb);
            Marshal.Copy(bgraBytes, 0, data.Scan0, bgraBytes.Length);
            bitmap.UnlockBits(data);
            return bitmap;
        }

        private static string? FindFramingPlanPdf(string insFilePath)
        {
            string? inspectionsFolder = Path.GetDirectoryName(insFilePath);
            string? inspectionsRoot = inspectionsFolder != null ? Path.GetDirectoryName(inspectionsFolder) : null;
            string jobId = Path.GetFileNameWithoutExtension(insFilePath).Split('-').FirstOrDefault() ?? "";
            if (string.IsNullOrWhiteSpace(inspectionsRoot) || string.IsNullOrWhiteSpace(jobId))
                return null;

            string engineeringFolder = Path.Combine(inspectionsRoot, "Jobs", jobId, "Engineering");
            if (!Directory.Exists(engineeringFolder))
                return null;

            return Directory.GetFiles(engineeringFolder, "*.pdf", SearchOption.TopDirectoryOnly)
                .Select(path => new
                {
                    Path = path,
                    Revision = GetRevision(path, jobId),
                    Score = ScoreFramingPdf(path, jobId)
                })
                .Where(candidate => candidate.Score > 0)
                .OrderByDescending(candidate => candidate.Revision)
                .ThenByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase)
                .Select(candidate => candidate.Path)
                .FirstOrDefault();
        }

        private static int ScoreFramingPdf(string path, string jobId)
        {
            string name = Path.GetFileNameWithoutExtension(path).ToUpperInvariant();
            int score = 0;
            if (name.Contains("WITH DETAIL")) score += 300;
            if (name.Contains("ENGINEERING")) score += 180;
            if (name.Contains("FRAMING")) score += 150;
            if (Regex.IsMatch(name, $@"(?:^|\(){Regex.Escape(jobId)}(?:R\d+)?(?:\)|$)", RegexOptions.IgnoreCase)) score += 120;
            if (Regex.IsMatch(name, @"^\d+(?:R\d+)?$", RegexOptions.IgnoreCase)) score += 90;
            if (name.Contains("DETAIL")) score += 45;

            if (name.Contains("FOUNDATION") && !name.Contains("FRAMING")) score -= 240;
            if (Regex.IsMatch(name, @"(?:^|[\s(_-])\d+(?:R\d+)?FD\)?$", RegexOptions.IgnoreCase)) score -= 260;
            if (name.Contains("EC") || name.Contains("ENERGY")) score -= 180;
            if (name.Contains("FFP") || name.Contains("FOOTPRINT")) score -= 180;
            return score;
        }

        private static int GetRevision(string path, string jobId)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            var match = Regex.Match(name, $@"{Regex.Escape(jobId)}R(\d+)(?=[A-Za-z)]|\b)", RegexOptions.IgnoreCase);
            if (!match.Success)
                match = Regex.Match(name, @"(?:^|[^A-Za-z])R(\d+)\b", RegexOptions.IgnoreCase);
            return match.Success && int.TryParse(match.Groups[1].Value, out int revision) ? revision : 0;
        }

        private static string BuildCacheKey(string pdfPath)
        {
            var info = new FileInfo(pdfPath);
            string raw = $"{Path.GetFullPath(pdfPath)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{FramingDesignParser.ParserVersion}";
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
        }

        private static string GetCachePath(string cacheKey)
        {
            string directory = Path.Combine(AppIdentity.LocalAppDataPath, "framing-cache");
            return Path.Combine(directory, cacheKey + ".json");
        }

        private static FramingDesignInfo? TryLoadDiskCache(string pdfPath, string cacheKey)
        {
            try
            {
                string path = GetCachePath(cacheKey);
                if (!File.Exists(path))
                    return null;
                var cache = JsonConvert.DeserializeObject<FramingCacheEnvelope>(File.ReadAllText(path));
                if (cache == null || cache.ParserVersion != FramingDesignParser.ParserVersion)
                    return null;
                var file = new FileInfo(pdfPath);
                if (cache.PdfLength != file.Length || cache.PdfLastWriteUtcTicks != file.LastWriteTimeUtc.Ticks)
                    return null;
                return cache.Info;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Framing cache read failed: {ex.Message}");
                return null;
            }
        }

        private static void TrySaveDiskCache(string pdfPath, string cacheKey, FramingDesignInfo info)
        {
            try
            {
                string path = GetCachePath(cacheKey);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var file = new FileInfo(pdfPath);
                var envelope = new FramingCacheEnvelope
                {
                    ParserVersion = FramingDesignParser.ParserVersion,
                    PdfLength = file.Length,
                    PdfLastWriteUtcTicks = file.LastWriteTimeUtc.Ticks,
                    Info = info
                };
                string temporary = path + ".tmp";
                File.WriteAllText(temporary, JsonConvert.SerializeObject(envelope, Formatting.Indented));
                File.Move(temporary, path, overwrite: true);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Framing cache write failed: {ex.Message}");
            }
        }

        private sealed class FramingCacheEnvelope
        {
            public int ParserVersion { get; set; }
            public long PdfLength { get; set; }
            public long PdfLastWriteUtcTicks { get; set; }
            public FramingDesignInfo? Info { get; set; }
        }
    }
}
