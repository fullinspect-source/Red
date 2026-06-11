using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using DrawingImageFormat = System.Drawing.Imaging.ImageFormat;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Docnet.Core;
using Docnet.Core.Models;
using Tesseract;
using UglyToad.PdfPig;
using PdfPigPage = UglyToad.PdfPig.Content.Page;

namespace InspectionEditor.Services
{
    internal class SlabEngineeringInfo
    {
        public string? PdfPath { get; set; }
        public string? DisplayName { get; set; }
        public int? CableCount { get; set; }
        public int? SlabThicknessInches { get; set; }
        public int? BeamWidthInches { get; set; }
        public int? BeamDepthInches { get; set; }
        public int? HolddownCount { get; set; }
        public string? HolddownBreakdown { get; set; }
        public string? StatusText { get; set; }
        public string? DebugText { get; set; }
        public List<SlabEngineeringRevisionOption> RevisionOptions { get; set; } = new();
    }

    internal class SlabEngineeringRevisionOption
    {
        public string Label { get; set; } = "";
        public string FullPath { get; set; } = "";
        public int Revision { get; set; }
    }

    internal static class SlabEngineeringService
    {
        // Cached tessdata path — discovered once at startup
        private static string? _tessDataPath;
        private static bool _tessDataPathResolved;

        // ---------------------------------------------------------------
        // Public API
        // ---------------------------------------------------------------

        public static SlabEngineeringInfo GetInfoForInspection(string? insFilePath)
        {
            var info = new SlabEngineeringInfo();
            if (string.IsNullOrWhiteSpace(insFilePath) || !File.Exists(insFilePath))
            {
                info.StatusText = "No inspection file loaded.";
                return info;
            }

            string? jobFolder = GetJobFolder(insFilePath);
            if (string.IsNullOrWhiteSpace(jobFolder) || !Directory.Exists(jobFolder))
            {
                info.StatusText = "Job folder not found.";
                return info;
            }

            string engFolder = Path.Combine(jobFolder, "Engineering");
            if (!Directory.Exists(engFolder))
            {
                info.StatusText = "Engineering folder not found.";
                return info;
            }

            string jobId = Path.GetFileNameWithoutExtension(insFilePath).Split('-').FirstOrDefault() ?? "";
            var revisionOptions = FindSlabEngineeringRevisionOptions(engFolder, jobId);
            if (revisionOptions.Count == 0)
            {
                info.StatusText = "No slab engineering PDF found.";
                return info;
            }

            info.RevisionOptions = revisionOptions;
            var bestPdf = revisionOptions.First();
            info.PdfPath = bestPdf.FullPath;
            info.DisplayName = Path.GetFileName(bestPdf.FullPath);

            try
            {
                var data = ExtractAllFoundationData(bestPdf.FullPath);
                info.CableCount = data.CableCount;
                info.SlabThicknessInches = data.SlabThicknessInches;
                info.BeamWidthInches = data.BeamWidthInches;
                info.BeamDepthInches = data.BeamDepthInches;
                info.HolddownCount = data.HolddownCount;
                info.HolddownBreakdown = data.HolddownBreakdown;
                info.DebugText = data.DebugText;

                var parts = new List<string>();
                if (info.CableCount.HasValue) parts.Add($"Strands: {info.CableCount}");
                if (info.SlabThicknessInches.HasValue) parts.Add($"Slab: {info.SlabThicknessInches}\"");
                if (info.BeamWidthInches.HasValue && info.BeamDepthInches.HasValue)
                    parts.Add($"Beam: {info.BeamWidthInches}\"W × {info.BeamDepthInches}\"D");
                if (info.HolddownCount.HasValue) parts.Add($"Holddowns: {FormatHolddownDisplay(info)}");

                info.StatusText = parts.Count > 0
                    ? string.Join(" | ", parts)
                    : "Engineering data not found in slab PDF.";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Slab PDF parse error: {ex.Message}");
                info.StatusText = "Could not parse slab PDF.";
                info.DebugText = $"Parse error: {ex.Message}";
            }

            return info;
        }

        public static void OpenSlabEngineeringPdf(string? insFilePath)
        {
            var info = GetInfoForInspection(insFilePath);
            if (string.IsNullOrWhiteSpace(info.PdfPath) || !File.Exists(info.PdfPath))
                throw new FileNotFoundException(info.StatusText ?? "Slab engineering PDF not found.");
            OpenPdf(info.PdfPath);
        }

        public static void OpenPdf(string pdfPath)
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = pdfPath,
                UseShellExecute = true
            });
        }

        // ---------------------------------------------------------------
        // Extraction: OCR first, PdfPig text fallback for cable count
        // ---------------------------------------------------------------

        private static FoundationData ExtractAllFoundationData(string pdfPath)
        {
            var data = new FoundationData();
            string? tessData = GetTessDataPath();

            if (tessData != null)
            {
                ExtractViaOcr(pdfPath, tessData, data);
            }

            // PdfPig text extraction as fallback for cable count only
            // (works on PDFs with TrueType/OTF fonts, fails on AutoCAD SHX)
            if (data.CableCount == null)
            {
                data.CableCount = ExtractCableCountViaText(pdfPath);
                if (data.CableCount != null)
                    data.DebugText = (data.DebugText ?? "") + " [cable count from text extraction]";
            }

            if (tessData == null && data.CableCount == null)
                data.DebugText = "No tessdata found — OCR unavailable. Place eng.traineddata in app tessdata/ folder.";

            return data;
        }

        private static void ExtractViaOcr(string pdfPath, string tessDataPath, FoundationData data)
        {
            try
            {
                using var engine = new TesseractEngine(tessDataPath, "eng", EngineMode.Default);

                using var docReader = DocLib.Instance.GetDocReader(pdfPath, new PageDimensions(3.0));
                // Limit to 2 pages: the foundation plan is always page 1.
                // Pages 3+ are shear wall / framing sheets — they contain STHD
                // hardware references that create false holddown matches.
                int pageCount = Math.Min(docReader.GetPageCount(), 2);

                var allText = new System.Text.StringBuilder();
                var cropText = new System.Text.StringBuilder();

                for (int i = 0; i < pageCount; i++)
                {
                    using var pageReader = docReader.GetPageReader(i);
                    byte[] rawBytes = pageReader.GetImage();
                    int w = pageReader.GetPageWidth();
                    int h = pageReader.GetPageHeight();

                    string pageText = OcrBytes(rawBytes, w, h, engine);
                    allText.AppendLine(pageText);
                    allText.AppendLine("---PAGE_BREAK---");

                    // Crop to lower-left 60%×55% of the page — the elongation chart lives here.
                    // This isolates strand count from right-side table columns that garble it.
                    string pageCrop = OcrCroppedRegion(rawBytes, w, h, engine,
                        cropLeft: 0,
                        cropTop: (int)(h * 0.45),
                        cropWidth: (int)(w * 0.60),
                        cropHeight: (int)(h * 0.55));
                    cropText.AppendLine(pageCrop);
                    cropText.AppendLine("---CROP_BREAK---");
                }

                string fullText = allText.ToString();
                string strandCropText = cropText.ToString();

                // The focused elongation-chart crop is much less likely to contain
                // unrelated drawing/detail numbers, so let it win when it can.
                data.CableCount = ExtractStrandCountFromCrop(strandCropText);

                ParseFoundationData(fullText, data);

                // Retry strand count using the full OCR if the focused crop missed it.
                if (data.CableCount == null)
                    data.CableCount = ExtractStrandCountFromCrop(fullText);

                // Write full OCR text + crop to temp file for pattern debugging
                string debugFile = Path.Combine(Path.GetTempPath(), "red_ocr_debug.txt");
                File.WriteAllText(debugFile,
                    "=== FULL PAGE OCR ===\n" + fullText +
                    "\n\n=== LOWER-LEFT CROP OCR (strand search) ===\n" + strandCropText);

                data.DebugText = $"OCR pages={pageCount}, chars={fullText.Length} | full text → {debugFile}";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"OCR extraction failed: {ex.Message}");
                data.DebugText = $"OCR error: {ex.Message}";
            }
        }

        private static string OcrBytes(byte[] bgraBytes, int width, int height, TesseractEngine engine)
        {
            try
            {
                using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                var bmpData = bmp.LockBits(
                    new Rectangle(0, 0, width, height),
                    ImageLockMode.WriteOnly,
                    PixelFormat.Format32bppArgb);
                Marshal.Copy(bgraBytes, 0, bmpData.Scan0, bgraBytes.Length);
                bmp.UnlockBits(bmpData);

                using var ms = new MemoryStream();
                bmp.Save(ms, DrawingImageFormat.Png);

                using var pix = Pix.LoadFromMemory(ms.ToArray());
                using var page = engine.Process(pix);
                return page.GetText() ?? "";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"OCR page error: {ex.Message}");
                return "";
            }
        }

        private static string OcrCroppedRegion(byte[] bgraBytes, int width, int height,
            TesseractEngine engine, int cropLeft, int cropTop, int cropWidth, int cropHeight)
        {
            try
            {
                cropWidth = Math.Min(cropWidth, width - cropLeft);
                cropHeight = Math.Min(cropHeight, height - cropTop);
                if (cropWidth <= 0 || cropHeight <= 0) return "";

                using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                var bmpData = bmp.LockBits(
                    new Rectangle(0, 0, width, height),
                    ImageLockMode.WriteOnly,
                    PixelFormat.Format32bppArgb);
                Marshal.Copy(bgraBytes, 0, bmpData.Scan0, bgraBytes.Length);
                bmp.UnlockBits(bmpData);

                using var crop = bmp.Clone(
                    new Rectangle(cropLeft, cropTop, cropWidth, cropHeight),
                    PixelFormat.Format32bppArgb);

                using var ms = new MemoryStream();
                crop.Save(ms, DrawingImageFormat.Png);

                using var pix = Pix.LoadFromMemory(ms.ToArray());
                using var page = engine.Process(pix);
                return page.GetText() ?? "";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"OCR crop error: {ex.Message}");
                return "";
            }
        }

        private static int? ExtractStrandCountFromCrop(string cropText)
        {
            // In the cropped lower-left region the elongation table has less noise.
            // Try most-specific patterns first to avoid grabbing per-route counts
            // (e.g. 12 for S-52) instead of the TOTAL NUMBER OF STRANDS summary.
            var patterns = new[]
            {
                // Most specific: "TOTAL NUMBER OF STRANDS" summary row
                (@"TOTAL\s+NUMBER\s+OF\s+STRANDS[\s\S]{0,300}?\b(\d{1,3})\b", RegexOptions.IgnoreCase),
                // OCR-garbling: total row can become "TON SAEs 25" just before
                // "TOTAL LINEAR / FEET OF STRANDS". Capture that first number.
                (@"\b(?:TOTAL|TON|T0N)\b[^\n]{0,60}?\b(?:SAE\w*|STRANDS?)\b[^\n]{0,60}?\b(\d{2,3})\b[\s\S]{0,100}?TOTAL\s+LINEAR[\s\S]{0,100}?FEET\s+OF\s+STRANDS", RegexOptions.IgnoreCase),
                // Tight: number within 30 chars of "OF STRANDS", not followed by foot/fraction marks
                (@"OF\s+STRANDS[\s\S]{0,30}?(\d{1,3})(?!\s*['/\""\d])", RegexOptions.IgnoreCase),
                // Medium: number anywhere between "TOTAL NUMBER" and "TOTAL LINEAR"
                (@"TOTAL\s+NUMBER[\s\S]{0,200}?(\d{1,3})[\s\S]{0,300}?TOTAL\s+LINEAR", RegexOptions.IgnoreCase),
                // Loose: any reasonable strand count label
                (@"(?:NUMBER|TOTAL|COUNT)\s+OF\s+STRANDS[\s\S]{0,80}?(\d{1,3})", RegexOptions.IgnoreCase),
                // Single-column: count sits between two TOTAL keywords on one line.
                (@"\bTOTAL\b.{2,30}?\b(\d{2,3})\b.{1,10}\bTOTAL\b", RegexOptions.IgnoreCase),
                // OCR-garbling: "TOTAL NUMBER OF STRANDS" collapses to word ending in ...ANDS
                (@"[A-Z]{4,}ANDS\s{0,15}(\d{2,3})\b(?!\s*['/])", RegexOptions.None),
                // Last resort: "STRANDS" and first small number nearby
                (@"STRANDS\s{0,5}(\d{1,3})(?!\s*['/\""\d])", RegexOptions.IgnoreCase),
            };

            foreach (var (pat, opts) in patterns)
            {
                var m = Regex.Match(cropText, pat, opts);
                if (m.Success && int.TryParse(m.Groups[1].Value, out int c) && c >= 10 && c <= 400)
                    return c;
            }
            return null;
        }

        // ---------------------------------------------------------------
        // Pattern matching on OCR text
        // ---------------------------------------------------------------

        private static void ParseFoundationData(string text, FoundationData data)
        {
            // ── 1. Cable / strand count ─────────────────────────────────
            // The elongation chart has a column header "NUMBER OF STRANDS" above
            // per-route counts (1, 1, 2, 2, 12...) and a summary row at the bottom:
            //   "TOTAL NUMBER OF STRANDS   53"
            //   "TOTAL LINEAR FEET OF STRANDS   3049"
            // CRITICAL: try the most-specific pattern first. The column header
            // "OF STRANDS" appears before per-route counts (e.g. 12 for S-52),
            // so tight/medium patterns tried first would grab 12 instead of 53.
            if (data.CableCount == null)
            {
                // Most specific: "TOTAL NUMBER OF STRANDS" summary row.
                // Use \b word boundaries so (\d{1,3}) never greedily matches a prefix of
                // a longer number (e.g. "304" from "3049"). Widen gap to 300 chars to
                // accommodate OCR column-reordering that may put noise between the label
                // and the value. No negative lookahead needed — \b handles it.
                var m = Regex.Match(text,
                    @"TOTAL\s+NUMBER\s+OF\s+STRANDS[\s\S]{0,300}?\b(\d{1,3})\b",
                    RegexOptions.IgnoreCase);
                if (m.Success && int.TryParse(m.Groups[1].Value, out int c) && c >= 10 && c <= 400)
                    data.CableCount = c;
            }
            if (data.CableCount == null)
            {
                // Tight: number within 20 chars of "OF STRANDS", not followed by foot/fraction
                // marks, and cap at 200 to avoid grabbing lineal-footage values like "252'"
                var m = Regex.Match(text,
                    @"OF[\s_]+STRANDS[\s_]{0,20}\b(\d{1,3})\b(?!\s*['/\""\d])",
                    RegexOptions.IgnoreCase);
                if (m.Success && int.TryParse(m.Groups[1].Value, out int c) && c >= 10 && c <= 200)
                    data.CableCount = c;
            }
            if (data.CableCount == null)
            {
                // Medium: within the span from "OF STRANDS" to "TOTAL LINEAR", take
                // the first ≤200 number not followed by a foot/fraction mark.
                var m = Regex.Match(text,
                    @"OF[\s_]+STRANDS([\s\S]{0,200}?)TOTAL\s+LINEAR",
                    RegexOptions.IgnoreCase);
                if (m.Success)
                {
                    foreach (Match nm in Regex.Matches(m.Groups[1].Value,
                        @"\b(\d{1,3})\b(?!\s*['/\""\d])"))
                    {
                        if (int.TryParse(nm.Groups[1].Value, out int c) && c >= 10 && c <= 200)
                        { data.CableCount = c; break; }
                    }
                }
            }
            if (data.CableCount == null)
            {
                // Split-column fallback: OCR merges two table columns onto one line, producing:
                //   "TOTAL NUMBER  TOTAL: 745'"   ← two columns on one line
                //   "53"                          ← strand count alone on next line
                //   "OF_STRANDS"                  ← label continuation on next line
                // Match by requiring the count to be the SOLE token on its line,
                // immediately following a line that starts with TOTAL, and then
                // "OF" + optional punctuation + "STRANDS" within 60 chars after.
                var m = Regex.Match(text,
                    @"\bTOTAL\b[^\n]*\n[^\n]{0,10}\b(\d{2,3})\b[^\n]{0,10}\n[\s\S]{0,60}?OF[\s_]+STRANDS",
                    RegexOptions.IgnoreCase);
                if (m.Success && int.TryParse(m.Groups[1].Value, out int c) && c >= 10 && c <= 400)
                    data.CableCount = c;
            }
            if (data.CableCount == null)
            {
                // Last-resort: any 2-3 digit number between "TOTAL" and "OF.*STRANDS",
                // iterating all matches to skip lineal-footage values (e.g. 745) that
                // exceed the strand-count ceiling of 400.
                foreach (Match m in Regex.Matches(text,
                    @"\bTOTAL\b[\s\S]{0,100}?\b(\d{2,3})\b[\s\S]{0,50}?OF.{0,3}STRANDS",
                    RegexOptions.IgnoreCase))
                {
                    if (int.TryParse(m.Groups[1].Value, out int c) && c >= 10 && c <= 400)
                    { data.CableCount = c; break; }
                }
            }
            if (data.CableCount == null)
            {
                // Single-column chart: TOTAL row has count between two TOTAL keywords.
                // OCR example: "TOTAL. ANOMBER 53 TOTAL: 745)"
                // The count (≤400) sits between the first TOTAL (number col total)
                // and the second TOTAL (linear-feet col total, typically ≥ 500).
                var m = Regex.Match(text,
                    @"\bTOTAL\b.{2,30}?\b(\d{2,3})\b.{1,10}\bTOTAL\b",
                    RegexOptions.IgnoreCase);
                if (m.Success && int.TryParse(m.Groups[1].Value, out int c) && c >= 10 && c <= 400)
                    data.CableCount = c;
            }
            if (data.CableCount == null)
            {
                // OCR-garbling fallback: "TOTAL NUMBER OF STRANDS" sometimes collapses to one
                // garbled word ending in ...ANDS (e.g. "TOPNSTNANDS 41"). Match any all-caps
                // word (8+ chars) ending in ANDS directly followed by a 2-3 digit count.
                var m = Regex.Match(text,
                    @"[A-Z]{4,}ANDS\s{0,15}(\d{2,3})\b(?!\s*['/])",
                    RegexOptions.None);
                if (m.Success && int.TryParse(m.Groups[1].Value, out int c) && c >= 10 && c <= 400)
                    data.CableCount = c;
            }
            if (data.CableCount == null)
            {
                // Summing fallback: locate the Elongation Chart section and sum
                // the NUMBER column from individual strand rows.
                data.CableCount = TrySumElongationTable(text);
            }

            // ── 2. Slab thickness ───────────────────────────────────────
            // Note format: "Slab shall be  _4"_  thick, U.N.O."
            // OCR likely:  "Slab shall be 4" thick" or "Slab shall be 4 thick"
            // The fill-in blank may produce underscores, spaces, or nothing.
            // Use a bridge of up to 20 non-letter chars between "be" and the number,
            // and up to 20 between the number and "thick".
            var slabMatch = Regex.Match(text,
                @"[Ss]lab\s+shall\s+be[^a-zA-Z]{0,20}?(\d{1,2})[^a-zA-Z]{0,20}thick",
                RegexOptions.IgnoreCase);
            if (slabMatch.Success
                && int.TryParse(slabMatch.Groups[1].Value, out int slabT)
                && slabT >= 3 && slabT <= 12)
            {
                data.SlabThicknessInches = slabT;
            }

            // ── 3. Beam width × depth ────────────────────────────────────
            // Note format: "Beams shall be  _10"_  "W" x  _25"_  "D""
            // OCR likely:  "Beams shall be 10" "W" x 25" "D"" (inch marks and quotes vary)
            // Strategy: capture first number after "Beams shall be" (= width),
            //           then next number after W...x (= depth), stopping before newline.
            var beamMatch = Regex.Match(text,
                @"[Bb]eams?\s+shall\s+be[^0-9\n]{0,20}(\d{1,2})[^0-9\n]{0,20}[Ww][^0-9\n]{0,20}[xX×][^0-9\n]{0,20}(\d{1,2})",
                RegexOptions.IgnoreCase);
            if (!beamMatch.Success)
            {
                // Looser multi-line fallback
                beamMatch = Regex.Match(text,
                    @"[Bb]eam[\s\S]{0,40}?(\d{1,2})[^0-9]{0,15}[Ww][^0-9]{0,15}[xX×][^0-9]{0,15}(\d{1,2})[^0-9]{0,10}[Dd]",
                    RegexOptions.IgnoreCase | RegexOptions.Singleline);
            }
            if (beamMatch.Success)
            {
                if (int.TryParse(beamMatch.Groups[1].Value, out int bw) && bw >= 6 && bw <= 36)
                    data.BeamWidthInches = bw;
                if (int.TryParse(beamMatch.Groups[2].Value, out int bd) && bd >= 8 && bd <= 60)
                    data.BeamDepthInches = bd;
            }

            // ── 4. Holddown count ────────────────────────────────────────
            data.HolddownCount = ExtractHolddownCount(text, out string? holddownBreakdown);
            data.HolddownBreakdown = holddownBreakdown;
        }

        internal static string FormatHolddownDisplay(SlabEngineeringInfo info)
        {
            if (!info.HolddownCount.HasValue)
                return "not found";

            return !string.IsNullOrWhiteSpace(info.HolddownBreakdown)
                ? $"{info.HolddownCount} = {info.HolddownBreakdown}"
                : info.HolddownCount.Value.ToString();
        }

        private static int? ExtractHolddownCount(string text, out string? breakdown)
        {
            breakdown = null;
            var quantities = new List<int>();

            // ── Primary: match embedded holddown table rows ─────────────────
            // Some plans OCR as:
            //   10 = STHD-10/STAD-10  1
            //   14 = STHD-14/STAD-14  7
            // Sum every row instead of stopping on the first hardware quantity.
            var embeddedRowsPattern = new Regex(
                @"(?:^|[\r\n])\s*(?:\[\s*\d{1,2}\s*\]|\d{1,2})?\s*=?\s*(?:STHD|STAD|HTT|MST|HDU|PHD|LSTA|MSTA|CS(?:TH|16|20))[\w/-]*(?:\s*/\s*(?:STHD|STAD|HTT|MST|HDU|PHD|LSTA|MSTA|CS(?:TH|16|20))[\w/-]*)*\s+(?<qty>\d{1,3})(?=\s*(?:\r?\n|$))",
                RegexOptions.IgnoreCase);

            foreach (Match m in embeddedRowsPattern.Matches(text))
            {
                if (int.TryParse(m.Groups["qty"].Value, out int qty) && qty >= 1 && qty <= 200)
                    quantities.Add(qty);
            }
            if (quantities.Count > 0)
            {
                breakdown = string.Join("+", quantities);
                return quantities.Sum();
            }

            // ── Primary legacy: match the embedded holddowns table format ───
            // Strand plans use "(H) = HARDWARE_CODE   QTY" for each holddown type.
            // This format is unique to the embedded holddowns table and does NOT
            // appear in shear wall hardware schedules (which use "Number of ..." prefix).
            // OCR may read "(H)" as "(H)", "CH)", "(4)", etc. — try a few variants.
            var tablePattern = new Regex(
                @"\([Hh4]\)\s*=\s*(?:STHD|STAD|HTT|MST|HDU|PHD|LSTA|MSTA|CS(?:TH|16|20))[\w/-]*\s+(\d{1,3})",
                RegexOptions.IgnoreCase);

            int total = 0;
            foreach (Match m in tablePattern.Matches(text))
            {
                if (int.TryParse(m.Groups[1].Value, out int qty) && qty >= 1 && qty <= 200)
                {
                    total += qty;
                    quantities.Add(qty);
                }
            }
            if (total > 0)
            {
                breakdown = string.Join("+", quantities);
                return total;
            }

            // ── Fallback: scoped section search ────────────────────────────
            // OCR often garbles "Holddowns" (e.g. "Hokdowns"), so use a fuzzy
            // pattern: "Embedded" + 1-12 word/space chars + "down". Cap at 600
            // chars to avoid absorbing the rest of the document.
            var sectionMatch = Regex.Match(text,
                @"Embedded[\s\w]{1,12}down.{0,600}",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            string searchArea = sectionMatch.Success ? sectionMatch.Value : "";

            if (!string.IsNullOrEmpty(searchArea))
            {
                var holdownPattern = new Regex(
                    @"(?:STHD|STAD|HTT|MST|HDU|PHD|LSTA|MSTA|CS(?:TH|16|20))[\w/-]*\s+(\d{1,3})",
                    RegexOptions.IgnoreCase);

                foreach (Match m in holdownPattern.Matches(searchArea))
                {
                    if (int.TryParse(m.Groups[1].Value, out int qty) && qty >= 1 && qty <= 100)
                    {
                        total += qty;
                        quantities.Add(qty);
                    }
                }

                if (total == 0)
                {
                    foreach (var line in searchArea.Split('\n'))
                    {
                        if (!Regex.IsMatch(line, @"STHD|STAD|HDU|HTT|MST|STRAP|HOLD\s*DOWN", RegexOptions.IgnoreCase))
                            continue;
                        var eolNum = Regex.Match(line.TrimEnd(), @"(\d{1,3})\s*$");
                        if (eolNum.Success && int.TryParse(eolNum.Groups[1].Value, out int qty) && qty >= 1 && qty <= 100)
                        {
                            total += qty;
                            quantities.Add(qty);
                        }
                    }
                }
            }

            if (total > 0)
                breakdown = string.Join("+", quantities);

            return total > 0 ? total : null;
        }

        // ---------------------------------------------------------------
        // PdfPig text extraction fallback (for cable count on TrueType PDFs)
        // ---------------------------------------------------------------

        private static int? ExtractCableCountViaText(string pdfPath)
        {
            try
            {
                using var doc = PdfDocument.Open(pdfPath);
                var pages = doc.GetPages().Take(8).ToList();
                var text = string.Join("\n", pages.Select(p => p.Text));
                string targeted = string.Join("\n", pages.Select(GetPreferredRegionText));
                string combined = string.IsNullOrWhiteSpace(targeted) ? text : (targeted + "\n" + text);
                if (string.IsNullOrWhiteSpace(combined)) return null;

                var patterns = new[]
                {
                    @"TOTAL\s+NUMBER\s+OF\s+STRANDS\s*[:#]?\s*(\d{1,3})",
                    @"TOTAL\s+NO\.?\s+OF\s+STRANDS\s*[:#]?\s*(\d{1,3})",
                    @"TOTAL\s+(?:NUMBER\s+)?(?:OF\s+)?CABLES\s*[:#]?\s*(\d{1,3})",
                    @"CABLE\s+COUNT\s*[:#]?\s*(\d{1,3})",
                    @"NUMBER\s+OF\s+CABLES\s*[:#]?\s*(\d{1,3})",
                    @"TOTAL\s*CABLES\s*[:#]?\s*(\d{1,3})",
                };

                foreach (var pat in patterns)
                {
                    var m = Regex.Match(combined, pat, RegexOptions.IgnoreCase | RegexOptions.Singleline);
                    if (m.Success && int.TryParse(m.Groups[1].Value, out int count) && count >= 10 && count <= 400)
                        return count;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"PdfPig fallback error: {ex.Message}");
            }
            return null;
        }

        private static string GetPreferredRegionText(PdfPigPage page)
        {
            try
            {
                double width = page.Width;
                double height = page.Height;
                var regionWords = page.GetWords()
                    .Where(w => w.BoundingBox.Left >= 0 &&
                                w.BoundingBox.Right <= width * 0.65 &&
                                w.BoundingBox.Bottom >= 0 &&
                                w.BoundingBox.Top <= height * 0.55)
                    .ToList();

                if (regionWords.Count == 0) return page.Text ?? "";

                return string.Join("\n", regionWords
                    .GroupBy(w => Math.Round(w.BoundingBox.Bottom / 8.0) * 8.0)
                    .OrderByDescending(g => g.Key)
                    .Select(g => string.Join(" ", g.OrderBy(w => w.BoundingBox.Left).Select(w => w.Text))));
            }
            catch
            {
                return page.Text ?? "";
            }
        }

        // ---------------------------------------------------------------
        // Job/PDF discovery (unchanged logic)
        // ---------------------------------------------------------------

        private static string? GetJobFolder(string insFilePath)
        {
            string? insFolder = Path.GetDirectoryName(insFilePath);
            string? inspectionsFolder = insFolder != null ? Path.GetDirectoryName(insFolder) : null;
            string jobsFolder = inspectionsFolder != null ? Path.Combine(inspectionsFolder, "Jobs") : "";
            string jobId = Path.GetFileNameWithoutExtension(insFilePath).Split('-').FirstOrDefault() ?? "";
            if (string.IsNullOrWhiteSpace(jobsFolder) || string.IsNullOrWhiteSpace(jobId))
                return null;
            return Path.Combine(jobsFolder, jobId);
        }

        private static List<SlabEngineeringRevisionOption> FindSlabEngineeringRevisionOptions(string engFolder, string jobId)
        {
            return Directory.GetFiles(engFolder, "*.pdf")
                .Select(path => new
                {
                    Path = path,
                    Revision = GetRevision(path, jobId),
                    Score = ScoreSlabPdf(path)
                })
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Revision)
                .ThenByDescending(x => x.Score)
                .ThenBy(x => x.Path)
                .Select(x => new SlabEngineeringRevisionOption
                {
                    Revision = x.Revision,
                    FullPath = x.Path,
                    Label = x.Revision == 0
                        ? $"Base - {Path.GetFileName(x.Path)}"
                        : $"R{x.Revision} - {Path.GetFileName(x.Path)}"
                })
                .ToList();
        }

        private static int ScoreSlabPdf(string filePath)
        {
            string name = Path.GetFileNameWithoutExtension(filePath).ToUpperInvariant();
            int score = 0;

            if (Regex.IsMatch(name, @"\(\d+(?:R\d+)?FD\)$", RegexOptions.IgnoreCase)) score += 320;
            if (Regex.IsMatch(name, @"(?:^|[\s(_-])\d+(?:R\d+)?FD\)?$", RegexOptions.IgnoreCase)) score += 280;
            if (Regex.IsMatch(name, @"^\d+$", RegexOptions.IgnoreCase)) score += 110;
            if (Regex.IsMatch(name, @"^\d+\s*\(WITH DETAIL SHEETS\)$", RegexOptions.IgnoreCase)) score += 90;

            if (name.Contains("FOUNDATION DESIGN")) score += 80;
            if (name.Contains("FOUNDATION")) score += 18;
            if (name.Contains("SLAB")) score += 16;
            if (name.Contains("POST") && name.Contains("TENSION")) score += 14;
            if (name.Contains("CABLE")) score += 12;
            if (name.EndsWith("FD")) score += 80;
            if (name.Contains("FFP") || name.Contains("FOOTPRINT") || name.Contains("FOOT PRINT")) score -= 160;

            if (name.Contains("FDD")) score -= 120;
            if (name.Contains("DETAIL")) score -= 30;
            if (name.Contains("FRD")) score -= 60;
            if (name.Contains("FRAMING")) score -= 40;
            if (name.Contains("FRT") || name.EndsWith("FR")) score -= 40;
            if (name.Contains("STUD")) score -= 40;
            if (name.Contains("SW")) score -= 35;
            if (name.Contains("PESR")) score -= 35;
            if (name.Contains("EL") || name.Contains("EC") || name.Contains("ARCH")) score -= 35;
            if (name.Contains("FJ")) score -= 25;

            return score;
        }

        private static int GetRevision(string filePath, string jobId)
        {
            string name = Path.GetFileNameWithoutExtension(filePath);
            var match = Regex.Match(name, $@"{Regex.Escape(jobId)}R(\d+)", RegexOptions.IgnoreCase);
            return match.Success && int.TryParse(match.Groups[1].Value, out int rev) ? rev : 0;
        }

        // ---------------------------------------------------------------
        // Tessdata path discovery
        // ---------------------------------------------------------------

        private static string? GetTessDataPath()
        {
            if (_tessDataPathResolved) return _tessDataPath;
            _tessDataPathResolved = true;

            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            var candidates = new[]
            {
                Path.Combine(appDir, "tessdata"),
                Path.Combine(appDir, "..", "tessdata"),
                @"C:\Program Files\Tesseract-OCR\tessdata",
                @"C:\Program Files (x86)\Tesseract-OCR\tessdata",
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "Tesseract-OCR", "tessdata"),
            };

            _tessDataPath = candidates.FirstOrDefault(p =>
                Directory.Exists(p) && File.Exists(Path.Combine(p, "eng.traineddata")));

            Debug.WriteLine(_tessDataPath != null
                ? $"Tessdata found at: {_tessDataPath}"
                : "Tessdata not found — OCR unavailable");

            return _tessDataPath;
        }

        private static int? TrySumElongationTable(string text)
        {
            // Find the Elongation Chart section
            var chartMatch = Regex.Match(text, @"Elongation\s+Chart", RegexOptions.IgnoreCase);
            if (!chartMatch.Success) return null;

            string section = text.Substring(chartMatch.Index);

            // Cut at "TOTAL LINEAR" or "FEET OF STRANDS" or "CUBIC YARDS" — not bare "TOTAL",
            // which can fire on holddown-box totals that appear in OCR at the same vertical band.
            var totalMatch = Regex.Match(section,
                @"\bTOTAL\s+LINEAR\b|\bFEET\s+OF\s+STRANDS\b|\bCUBIC\s+YARDS\b",
                RegexOptions.IgnoreCase);
            if (totalMatch.Success)
                section = section.Substring(0, totalMatch.Index);

            // Match strand rows: LABEL COUNT
            // Valid labels: S-nn, BS-nn, $-nn, 8S-nn — single or double prefix only.
            // Tight pattern avoids "AD-14" false matches from holddown hardware tables.
            int total = 0;
            int rowCount = 0;
            foreach (Match m in Regex.Matches(section,
                @"(?:[B8$]?[S$]-\d{2,3})\s+(\d{1,2})(?:\s|$)",
                RegexOptions.IgnoreCase))
            {
                if (int.TryParse(m.Groups[1].Value, out int count) && count >= 1 && count <= 50)
                {
                    total += count;
                    rowCount++;
                }
            }

            return (rowCount >= 3 && total >= 10 && total <= 400) ? total : null;
        }
    }

    // ---------------------------------------------------------------
    // Internal data transfer object for extraction results
    // ---------------------------------------------------------------

    internal class FoundationData
    {
        public int? CableCount { get; set; }
        public int? SlabThicknessInches { get; set; }
        public int? BeamWidthInches { get; set; }
        public int? BeamDepthInches { get; set; }
        public int? HolddownCount { get; set; }
        public string? HolddownBreakdown { get; set; }
        public string? DebugText { get; set; }
    }
}
