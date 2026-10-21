using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using PdfSharp;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace InspectionEditor.Services.Reports
{
    /// <summary>
    /// F-022 §5a layout contract, drawn with PDFsharp. Coordinates are hundredths of an inch (u) inside the 0.5 in margins
    /// (printable width 750 u); 1 u = 0.72 pt. Bands that must not split move whole to the next page (LA13).
    /// </summary>
    public sealed class ReportRenderer : IDisposable
    {
        private const double U = 0.72, Margin = 36, PageHeightU = 1000, FooterU = 39, PageHeaderU = 30, Width = 750;
        private static readonly CultureInfo EnUs = CultureInfo.GetCultureInfo("en-US");
        public static readonly XColor Caption = XColor.FromArgb(109, 110, 113), Value = XColor.FromArgb(65, 64, 66),
            Rule = XColor.FromArgb(157, 159, 162), Bar = XColor.FromArgb(209, 211, 212), Box = XColor.FromArgb(235, 235, 235),
            Accent = XColor.FromArgb(241, 91, 64), FailText = XColor.FromArgb(241, 90, 64), Green = XColor.FromArgb(0x2e, 0xcc, 0x71),
            Red = XColor.FromArgb(0xFF, 0, 0), Grey = XColor.FromArgb(0x80, 0x80, 0x80), StatusPass = XColor.FromArgb(123, 204, 143);

        private readonly PdfDocument _doc = new();
        private readonly List<PdfPage> _reportPages = new();
        private readonly ReportDocument _report;
        private readonly ReportSettings _settings;
        private readonly DateTime _printDate;
        private XGraphics? _g;
        private double _y;
        private readonly XFont _base, _baseItalic, _small, _title, _titleItalic, _status, _nho, _symbols, _next;

        private ReportRenderer(ReportDocument report, ReportSettings settings, DateTime printDate)
        {
            ReportFonts.Install();
            _report = report;
            _settings = settings;
            _printDate = printDate;
            _base = new XFont(ReportFonts.Base, 9.75);
            _baseItalic = new XFont(ReportFonts.Base, 9.75, XFontStyleEx.Italic);
            _small = new XFont(ReportFonts.Base, 8.25);
            _title = new XFont(ReportFonts.Base, 15);
            _titleItalic = new XFont(ReportFonts.Base, 15, XFontStyleEx.Italic);
            _status = new XFont(ReportFonts.Base, 14.25, XFontStyleEx.Bold);
            _nho = new XFont(ReportFonts.Nho, 8.25);
            _symbols = new XFont(ReportFonts.Symbols, 8.25);
            _next = new XFont(ReportFonts.Base, 10.5);
        }

        /// <summary>Renders the report, the City of Kyle page and the attachments (A1) into one PDF.</summary>
        public static byte[] Render(ReportDocument report, ReportSettings settings, ReportOptions options, DateTime printDate, out int reportPages)
        {
            using var renderer = new ReportRenderer(report, settings, printDate);
            byte[] pdf = renderer.RenderAll(options);
            reportPages = renderer._reportPages.Count;
            return pdf;
        }

        private byte[] RenderAll(ReportOptions options)
        {
            NewPage();
            switch (_report.Kind)
            {
                case ReportKind.Nho: RenderNho(); break;
                case ReportKind.SpecialConsult: RenderSpecialConsult(); break;
                default: RenderDefault(); break;
            }
            _g?.Dispose();
            _g = null;
            FinishPages();
            if (_report.Kyle) RenderKyle();
            if (options.Attachments) AppendAttachments();
            using var stream = new MemoryStream();
            _doc.Save(stream, false);
            return stream.ToArray();
        }

        // ── Pages ──────────────────────────────────────────────────────────────────────────────────────────────────

        private XGraphics G => _g!;

        private void NewPage()
        {
            _g?.Dispose();
            var page = _doc.AddPage();
            page.Size = PageSize.Letter;
            page.Orientation = PageOrientation.Portrait;
            _reportPages.Add(page);
            _g = XGraphics.FromPdfPage(page);
            _y = 0;
            if (_reportPages.Count > 1)
            {
                // LA3: InspectionNumber, italic, caption colour, on every page but the first.
                Text(_report.H("InspectionNumber"), _baseItalic, Caption, 0, 0, 411.5, 20.6);
                _y = PageHeaderU;
            }
        }

        private double Bottom => PageHeightU - FooterU;

        // LA13: a band that does not fit in the space left starts a new page.
        private void Ensure(double heightU)
        {
            double top = _reportPages.Count > 1 ? PageHeaderU : 0;
            if (_y + heightU > Bottom && _y > top) NewPage();
        }

        // LA4 footers and LA12 stamp, once the page count is known.
        private void FinishPages()
        {
            int total = _reportPages.Count;
            byte[]? stamp = ReportSettings.ReadImage(_settings.StampPath);
            for (int i = 0; i < total; i++)
            {
                using var g = XGraphics.FromPdfPage(_reportPages[i], XGraphicsPdfPageOptions.Append);
                double footerTop = Bottom;
                g.DrawLine(new XPen(Accent, 1), P(0), P(footerTop + 11), P(Width), P(footerTop + 11));
                DrawText(g, $"Page {i + 1} of {total}", _base, Caption, 650, footerTop + 16, 100, 23, XStringFormats.CenterRight);
                if (_report.IsPreinspection) DrawStamp(g, stamp);
            }
        }

        // LA12: the owner's stamp artwork when supplied; otherwise RED writes "Pre-Inspection" itself (no INSPECT artwork).
        private void DrawStamp(XGraphics g, byte[]? stamp)
        {
            var state = g.Save();
            if (stamp != null && TryImage(stamp, out var image))
            {
                using (image)
                {
                    double w = P(Width), h = P(PageHeightU);
                    double scale = Math.Min(w / image!.PointWidth, h / image.PointHeight);
                    g.DrawImage(image, P(0) + (w - image.PointWidth * scale) / 2, P(0) + (h - image.PointHeight * scale) / 2, image.PointWidth * scale, image.PointHeight * scale);
                }
            }
            else
            {
                g.TranslateTransform(306, 396);
                g.RotateTransform(-35);
                var font = new XFont(ReportFonts.Base, 72, XFontStyleEx.Bold);
                g.DrawString("Pre-Inspection", font, new XSolidBrush(XColor.FromArgb(79, 128, 128, 128)), new XRect(-300, -50, 600, 100), XStringFormats.Center);
            }
            g.Restore(state);
        }

        private static double P(double u) => Margin + u * U; // u inside the printable area → page points

        // ── Primitives (x, y in u relative to the current band top _y) ─────────────────────────────────────────────

        private void Text(string text, XFont font, XColor color, double x, double y, double w, double h, XStringFormat? format = null) =>
            DrawText(G, text, font, color, x, _y + y, w, h, format ?? XStringFormats.CenterLeft);

        private static void DrawText(XGraphics g, string text, XFont font, XColor color, double x, double y, double w, double h, XStringFormat format)
        {
            if (string.IsNullOrEmpty(text)) return;
            var cell = new XRect(P(x) + 2 * U, P(y), w * U - 4 * U, h * U);
            var state = g.Save();
            g.IntersectClip(new XRect(cell.X, cell.Y - cell.Height, cell.Width, cell.Height * 3));
            g.DrawString(text, font, new XSolidBrush(color), cell, format);
            g.Restore(state);
        }

        private void Fill(XColor color, double x, double y, double w, double h) =>
            G.DrawRectangle(new XSolidBrush(color), P(x), P(_y + y), w * U, h * U);

        private void Border(XColor color, double x, double y, double w, double h) =>
            G.DrawRectangle(new XPen(color, 0.75), P(x), P(_y + y), w * U, h * U);

        private void HLine(XColor color, double x, double y, double w) =>
            G.DrawLine(new XPen(color, 0.75), P(x), P(_y + y), P(x + w), P(_y + y));

        private static bool TryImage(byte[] bytes, out XImage? image)
        {
            try { image = XImage.FromStream(new MemoryStream(bytes)); return true; }
            catch (Exception) { image = null; return false; }
        }

        // fit = keep the aspect ratio inside the box (ZoomImage); otherwise stretch.
        private void Picture(byte[]? bytes, double x, double y, double w, double h, bool fit)
        {
            if (bytes == null || !TryImage(bytes, out var image)) return;
            using (image)
            {
                double bw = w * U, bh = h * U;
                if (!fit) { G.DrawImage(image!, P(x), P(_y + y), bw, bh); return; }
                double scale = Math.Min(bw / image!.PointWidth, bh / image.PointHeight);
                double dw = image.PointWidth * scale, dh = image.PointHeight * scale;
                G.DrawImage(image, P(x) + (bw - dw) / 2, P(_y + y) + (bh - dh) / 2, dw, dh);
            }
        }

        // Word wrap into lines no wider than w (u); returns the lines.
        private List<string> Wrap(string text, XFont font, double w)
        {
            var lines = new List<string>();
            double max = (w - 4) * U;
            foreach (string paragraph in (text ?? "").Replace("\r\n", "\n").Split('\n'))
            {
                string line = "";
                foreach (string word in paragraph.Split(' '))
                {
                    string candidate = line.Length == 0 ? word : line + " " + word;
                    if (G.MeasureString(candidate, font).Width <= max || line.Length == 0)
                    {
                        line = candidate;
                    }
                    else
                    {
                        lines.Add(line);
                        line = word;
                    }
                    while (G.MeasureString(line, font).Width > max && line.Length > 1)
                    {
                        int cut = line.Length - 1;
                        while (cut > 1 && G.MeasureString(line.Substring(0, cut), font).Width > max) cut--;
                        lines.Add(line.Substring(0, cut));
                        line = line.Substring(cut);
                    }
                }
                lines.Add(line);
            }
            return lines;
        }

        private double LineU(XFont font) => font.GetHeight() / U;

        // Height (u) of wrapped text in a cell with a 23 u minimum.
        private double WrappedHeight(string text, XFont font, double w) => Math.Max(23, Wrap(text, font, w).Count * LineU(font) + 6);

        // Wrapped lines centred horizontally, and vertically or at the bottom of the cell (table headings).
        private void CenteredText(string text, XFont font, XColor color, double x, double y, double w, double h, bool bottom = false)
        {
            var lines = Wrap(text, font, w);
            double line = LineU(font);
            double top = bottom ? y + h - lines.Count * line - 1 : y + (h - lines.Count * line) / 2;
            foreach (string l in lines)
            {
                Text(l, font, color, x, top, w, line, XStringFormats.Center);
                top += line;
            }
        }

        private void WrappedText(string text, XFont font, XColor color, double x, double y, double w, double maxHeight = double.MaxValue)
        {
            double line = LineU(font);
            double top = y + 3;
            foreach (string l in Wrap(text, font, w))
            {
                if (top + line > y + maxHeight) break; // a fixed-height box clips (RL §4 note 3)
                Text(l, font, color, x, top, w, line, XStringFormats.TopLeft);
                top += line;
            }
        }

        // ── Default report ─────────────────────────────────────────────────────────────────────────────────────────

        private void RenderDefault()
        {
            Header(nho: false, specialConsult: false);
            if (_report.Sections.Count == 0)
            {
                Ensure(29.7);
                Text("No failed items.", _base, Value, 0, 0, Width, 29.7, XStringFormats.Center); // LA10
                _y += 29.7;
            }
            foreach (var section in _report.Sections)
            {
                if (section.Band != SpecialBand.None)
                {
                    if (section.BandPrints) SpecialBandSection(section); // otherwise the section prints nothing (P5)
                    continue;
                }
                SectionBar(section.Number, section.Name);
                ItemRows(section, pictureBox: (370, 208, 215.6, 0), onePerRow: false);
            }
            InspectorSignature(108.1);
        }

        // LA5/LA6/N9.
        private void Header(bool nho, bool specialConsult)
        {
            double shift = nho ? -3 : 0;
            Text(_report.H("InspectionName"), _title, XColors.Black, 0, 0, 411.5, 23);
            Text(_report.H("InspectionNumber"), _titleItalic, XColor.FromArgb(90, 90, 90), 0, 23, 411.5, 23);
            Picture(ReportSettings.ReadImage(_settings.LogoPath), 589.5, 0, 159.6, 20, fit: true);
            double capCX = nho ? 489.3 : specialConsult ? 492.4 : 489.7, capCW = nho ? 81.5 : specialConsult ? 78.3 : 81;
            double valBW = nho ? 154 : specialConsult ? 155.5 : 156.5;
            void Pair(string caption, string value, double y, double cx, double cw, double vx, double vw, bool rightCaption)
            {
                Text(caption, _base, Caption, cx, y + shift, cw, 23, rightCaption ? XStringFormats.CenterRight : XStringFormats.CenterLeft);
                Text(value, _base, Value, vx, y + shift, vw, 23);
                HLine(Rule, vx, y + shift + 23, vw);
            }
            Pair("Address:", _report.H("AddressCombo"), 63.3, 0, 77, 77, 412.7, false);
            Pair("Project:", _report.H("Project"), 86.3, 0, 77, 77, 412.7, false);
            Pair("Lot/Blk:", _report.H("LotBlock"), 109.3, 0, 77, 77, 179.5, false);
            Pair("Plan:", _report.H("PlanNumber"), 109.3, 266.5, 51.2, 322.75, valBW, true);
            Pair("Inspector:", _report.H("InspectorName"), 109.3, capCX, capCW, 575.75, 174.25, true);
            Pair("Client:", _report.H("BuilderName"), 132.3, 0, 77, 77, 179.5, false);
            Pair(_settings.JobCaption, _report.H("JobNumber"), 132.3, 222, 96, 322.75, valBW, true);
            Pair("Contact:", _report.H("Contact"), 132.3, capCX, capCW, 575.75, 174.25, true);
            Pair("Complete:", _report.H("Complete"), 155.3, 0, 77, 77, 179.5, false);
            Pair("PO:", _report.H("PO"), 155.3, 266.5, 51.2, 322.75, valBW, true);
            Pair("Contact #:", _report.H("ContactNumber"), 155.3, capCX, capCW, 575.75, 174.25, true);
            Pair("Structure:", _report.H("Structure"), 178.3, 0, 77, 77, 179.5, false);
            Pair("Super:", _report.H("Super"), 178.3, capCX, capCW, 575.75, 174.25, true);
            Pair("Notified:", _report.H("Notified"), 201.3, 0, 77, 77, nho ? 399.75 : 402.3, false);
            // LA6 status box.
            Fill(_report.StatusId == 3 ? Accent : StatusPass, 503.9, 63.3 + shift, 246.1, 38.7);
            Text(_report.H("Status"), _status, XColors.White, 503.9, 63.3 + shift, 246.1, 38.7, XStringFormats.Center);
            double nextY = nho ? 234.6 : specialConsult ? 238.9 : 237.3;
            Text(_report.H("NextAction"), _next, Value, 10, nextY, 730, 23, XStringFormats.Center);
            double band = nho ? 341.7 : specialConsult ? 275 : 269.8;
            if (nho)
            {
                if (_report.Variant == NhoVariant.Form97) band -= 62.6; // N9: FormId 97 removes the intro box
                else IntroBox();
            }
            _y += band;
        }

        // RL §3: T1, T2 and T5 take {0} = InspectorName and {1} = BuilderName; an approved text without them prints as stored.
        private string FixedText(string id)
        {
            string text = _settings.Text(id);
            if (text == ReportSettings.PendingText) return text;
            try { return string.Format(CultureInfo.InvariantCulture, text, _report.H("InspectorName"), _report.H("BuilderName")); }
            catch (FormatException) { return text; }
        }

        // N9.
        private void IntroBox() => TextBox(FixedText("T1"), 0, 269.2, 750, 62.6);

        private void TextBox(string text, double x, double y, double w, double h)
        {
            Fill(Box, x, y, w, h);
            Border(Value, x, y, w, h);
            WrappedText(text, _base, Value, x + 5, y + 2, w - 10, h - 4); // CanGrow false: clipped at the box
        }

        // LA7.
        private void SectionBar(string number, string name)
        {
            Ensure(29.2 + 23);
            Fill(Bar, 0, 0, Width, 24);
            string shown = double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) ? ((int)n).ToString(CultureInfo.InvariantCulture) : number;
            Text(shown, _base, Value, 5, 0, 33.75, 23);
            Text(name, _base, Value, 38.75, 0, 657, 23);
            _y += 29.2;
        }

        // The body height of a page after the running header (a band taller than this must be split).
        private double PageBody => Bottom - PageHeaderU;

        // LA8 + LA9 (Default) and S2 (Special Consult one per row). afterFirstRow: S2 draws the sketch after Items[0].
        private void ItemRows(ReportSection section, (double W, double H, double Pitch, double X0) pictureBox, bool onePerRow, Action? afterFirstRow = null)
        {
            string? previousNumber = null, previousName = null;
            bool first = true;
            foreach (var item in section.Items)
            {
                // P3: the whole value cell is coloured when Value is exactly "Fail" ("Fail - x" in rgb(241, 90, 64)).
                var (valuePart, fail, rest) = item.CombinedValue();
                string valueText = valuePart + rest;
                // LA8: a Number or a Name equal to the previous row's is left blank, each on its own.
                string number = item.Number != previousNumber ? item.Number : "";
                string name = item.Name != previousName ? item.Name : "";
                double h = Math.Max(Math.Max(WrappedHeight(name, _base, 305.2), WrappedHeight(valueText, _base, 379.8)), 23);
                if (h <= PageBody)
                {
                    Ensure(h);
                    Text(number, _base, Value, 5, 3, 37.7, LineU(_base), XStringFormats.TopLeft);
                    WrappedText(name, _base, Value, 42.75, 0, 305.2);
                    WrappedText(valueText, _base, fail ? FailText : Value, 370.2, 0, 379.8);
                    _y += h;
                }
                else
                {
                    // Taller than a page: split line by line, as XtraReports splits an oversize KeepTogether band.
                    var nameLines = Wrap(name, _base, 305.2);
                    var valueLines = Wrap(valueText, _base, 379.8);
                    double line = LineU(_base);
                    Ensure(line + 6);
                    Text(number, _base, Value, 5, 3, 37.7, line, XStringFormats.TopLeft);
                    _y += 3;
                    for (int i = 0; i < Math.Max(nameLines.Count, valueLines.Count); i++)
                    {
                        Ensure(line);
                        if (i < nameLines.Count) Text(nameLines[i], _base, Value, 42.75, 0, 305.2, line, XStringFormats.TopLeft);
                        if (i < valueLines.Count) Text(valueLines[i], _base, fail ? FailText : Value, 370.2, 0, 379.8, line, XStringFormats.TopLeft);
                        _y += line;
                    }
                    _y += 3;
                }
                previousNumber = item.Number;
                previousName = item.Name;
                Pictures(item.Pictures.Select(p => p.Bytes).ToList(), pictureBox, onePerRow);
                if (first) afterFirstRow?.Invoke();
                first = false;
            }
        }

        private void Pictures(List<byte[]> pictures, (double W, double H, double Pitch, double X0) box, bool onePerRow)
        {
            int perRow = onePerRow ? 1 : 2;
            for (int i = 0; i < pictures.Count; i += perRow)
            {
                Ensure(box.Pitch);
                for (int j = 0; j < perRow && i + j < pictures.Count; j++)
                    Picture(pictures[i + j], box.X0 + j * 380, 0, box.W, box.H, fit: true);
                _y += box.Pitch;
            }
        }

        // LA11.
        private void InspectorSignature(double band)
        {
            Ensure(band);
            Text("Inspector: ", _base, Caption, 370.2, 43.4, 88.5, 23, XStringFormats.BottomRight);
            Picture(_report.Signature, 466.7, 9.0, 283.3, 57.4, fit: false);
            HLine(Rule, 466.7, 66.4, 283.3);
            Text(_report.H("InspectorName"), _small, Value, 466.7, 66.4, 283.3, 20.9, XStringFormats.Center);
            Text(_report.H("InspectorPhone"), _small, Value, 465.75, 87.25, 283.3, 20.9, XStringFormats.Center);
            _y += band;
        }

        // LB1-LB3.
        private void SpecialBandSection(ReportSection section)
        {
            var items = section.Items;
            string V(int i) => i < items.Count ? items[i].Value : "";
            if (section.Band is SpecialBand.Slab or SpecialBand.PrePour)
            {
                double s = section.Band == SpecialBand.PrePour ? -2.7 : 0;
                double band = section.Band == SpecialBand.PrePour ? 201.0 : 210.4;
                Ensure(band);
                Fill(Bar, 0, 5 + s, Width, 24);
                Text(section.Number, _base, Value, 5, 5 + s, 33.75, 23);
                Text(section.Name, _base, Value, 38.75, 5 + s, 657, 23);
                Text("Exterior Measurements", _baseItalic, Caption, 96, 38 + s, 280.3, 20.6, XStringFormats.TopCenter);
                HLine(Rule, 96, 58.6 + s, 280.3);
                Text("Interior Measurements", _baseItalic, Caption, 425.4, 38 + s, 236.4, 20.6, XStringFormats.TopCenter);
                HLine(Rule, 425.4, 58.6 + s, 236.4);
                var columns = new[] { ("TOF to TOG", 220.2), ("TOF to BOB", 273.25), ("BW", 326.3), ("SD", 505.7), ("BW", 558.75), ("BD", 611.8) };
                foreach (var (heading, x) in columns)
                {
                    CenteredText(heading, _base, Caption, x, 64 + s, 50, 38, bottom: true);
                    HLine(Rule, x, 102 + s, 50);
                }
                string[] ext = { "Front Left Corner", "Front Right Corner", "Back Left Corner", "Back Right Corner" };
                double[] rows = { 102, 125, 148, 171 };
                for (int r = 0; r < 4; r++)
                {
                    Text(ext[r], _base, Caption, 96, rows[r] + s, 124, 23);
                    Text($"Location {r + 1}", _base, Caption, 425.4, rows[r] + s, 80, 23);
                    for (int c = 0; c < 3; c++)
                    {
                        Text(V(r * 3 + c), _base, Value, columns[c].Item2, rows[r] + s, 50, 23, XStringFormats.Center);
                        Text(V(12 + r * 3 + c), _base, Value, columns[3 + c].Item2, rows[r] + s, 50, 23, XStringFormats.Center);
                    }
                }
                _y += band;
                if (section.Band == SpecialBand.PrePour && section.PrePourPictures)
                {
                    Ensure(698.6);
                    double[] xs = { 0, 250.75, 499.9 }, ys = { 28, 197.4, 369.8, 541.3 }, caps = { 7.4, 176.8, 349.2, 520.7 };
                    for (int i = 0; i < 12; i++)
                    {
                        double x = xs[i % 3], y = ys[i / 3];
                        Text(items[i].Name, _baseItalic, Caption, x, caps[i / 3], 244, 20.6);
                        HLine(Rule, x, caps[i / 3] + 20.6, 244);
                        Picture(items[i].Pictures.FirstOrDefault()?.Bytes, x, y, 244, 142.4, fit: true);
                    }
                    _y += 698.6;
                }
                return;
            }
            // LB3 truck table.
            Ensure(593.75);
            Fill(Bar, 0, 5, Width, 24);
            Text(section.Number, _base, Value, 5, 5, 33.75, 23);
            Text(section.Name, _base, Value, 38.75, 5, 657, 23);
            var heads = new[] { ("Arrival", 65.8, 60.0), ("Gallons", 131.5, 60.0), ("Slump", 197.5, 60.0), ("Temp", 266.5, 57.0) };
            foreach (var (heading, x, w) in heads)
            {
                Text(heading, _base, Caption, x, 38.5, w, 21.3, XStringFormats.BottomCenter);
                HLine(Rule, x, 59.8, w);
            }
            Text("Placement Diagram", _base, Caption, 351.8, 38.5, 310, 21.3, XStringFormats.BottomLeft);
            for (int n = 1; n <= 15; n++)
            {
                double y = n <= 3 ? 60.8 + (n - 1) * 23 : 130.25 + (n - 4) * 23;
                Text($"Truck {n}", _base, Caption, 0, y, 61.5, 23);
                for (int c = 0; c < 4; c++)
                    Text(V(4 * (n - 1) + c), _base, Value, heads[c].Item2, y, heads[c].Item3, 23, XStringFormats.Center);
            }
            Border(Rule, 351.8, 63.7, 398.2, 515);
            Picture(_report.PlacementDiagram, 351.8, 63.7, 398.2, 515, fit: false);
            _y += 593.75;
        }

        // ── Special Consult (FormId 44) ────────────────────────────────────────────────────────────────────────────

        private void RenderSpecialConsult()
        {
            Header(nho: false, specialConsult: true);
            foreach (var section in _report.Sections)
            {
                SectionBar(section.Number, section.Name);
                // S2: section 2's Items[0].Pictures[0] is drawn once as the diagram, right after that item's row.
                Action? sketch = section.Number == "2" && _report.SketchDiagram != null ? () =>
                {
                    Ensure(610);
                    Border(Rule, 144.8, 0, 464, 600);
                    Picture(_report.SketchDiagram, 144.8, 0, 464, 600, fit: false);
                    _y += 610;
                } : null;
                ItemRows(section, pictureBox: (730, 400, 412.5, 10), onePerRow: true, afterFirstRow: sketch);
            }
            InspectorSignature(111.2);
        }

        // ── New Home Orientation ───────────────────────────────────────────────────────────────────────────────────

        private void RenderNho()
        {
            Header(nho: true, specialConsult: false);
            if (_report.Sections.Count == 0)
            {
                Ensure(29.7);
                Text("No failed items.", _base, Value, 0, 0, Width, 29.7, XStringFormats.Center);
                _y += 29.7;
            }
            foreach (var section in _report.Sections)
            {
                // N5 / RL L23: plain NHO and FormId 129 print the sign block in place of the Sign section (no bar, no rows);
                // with no Sign section (none in the form, or dropped by Only Show Defects) there is no sign block.
                if (section.IsSign && _report.Variant != NhoVariant.Form97)
                {
                    SignBlock();
                    continue;
                }
                SectionBar(section.Number, section.Name);
                foreach (var item in section.Items)
                {
                    // Line 1 wraps (LA2) and line 2 follows it; a block taller than a page is split line by line.
                    var line1 = Wrap(item.Line1, _nho, 711.25);
                    var line2 = item.Line2.Length > 0 ? Wrap(item.Line2, _nho, 711.25) : new List<string>();
                    double lineH = LineU(_nho);
                    double line1H = Math.Max(23, line1.Count * lineH + 6);
                    double h = Math.Max(52.1, line1H + (line2.Count > 0 ? Math.Max(23, line2.Count * lineH + 6) : 0));
                    if (h > PageBody)
                    {
                        Ensure(lineH + 6);
                        if (item.FirstOfGroup || !section.IsActionSection) Text(item.Number, _nho, XColors.Black, 0, 0, 38.7, 23);
                        _y += 3;
                        foreach (string l in line1.Concat(line2))
                        {
                            Ensure(lineH);
                            Text(l, _nho, XColors.Black, 38.75, 0, 711.25, lineH, XStringFormats.TopLeft);
                            _y += lineH;
                        }
                        _y += 3;
                        if (item.Pictures.Count > 0)
                        {
                            Pictures(item.Pictures.Select(p => p.Bytes).ToList(), (370, 208, 217.7, 0), onePerRow: false);
                            _y += 23;
                        }
                        continue;
                    }
                    Ensure(h + (item.Pictures.Count > 0 ? 217.7 : 0));
                    if (item.FirstOfGroup || !section.IsActionSection) Text(item.Number, _nho, XColors.Black, 0, 0, 38.7, 23);
                    if (line1.Count <= 1) Text(item.Line1, _nho, XColors.Black, 38.75, 0, 711.25, 23);
                    else WrappedText(item.Line1, _nho, XColors.Black, 38.75, 0, 711.25);
                    if (item.Indicator.Length > 0)
                    {
                        double x = 38.75 + 2 + G.MeasureString(item.Line1 + "  ", _nho).Width / U;
                        var (glyph, color) = item.Indicator switch
                        {
                            "cross-red" => ("❌", Red),
                            "cross-grey" => ("❌", Grey),
                            _ => ("✔", Green)
                        };
                        Text(glyph, _symbols, color, x, 0, 30, 23);
                    }
                    if (item.Line2.Length > 0) WrappedText(item.Line2, _nho, XColors.Black, 38.75, line1H, 711.25);
                    _y += h;
                    if (item.Pictures.Count > 0)
                    {
                        Pictures(item.Pictures.Select(p => p.Bytes).ToList(), (370, 208, 217.7, 0), onePerRow: false);
                        _y += 23;
                    }
                }
            }
            if (_report.Variant == NhoVariant.Form97) SignaturesBand();
            ActionItemsPage();
        }

        // N5.
        private void SignBlock()
        {
            if (_report.Variant == NhoVariant.Form97) return; // FormId 97 prints Sign as an ordinary section
            string date = _printDate.ToString("M/d/yyyy", EnUs);
            if (_report.Variant == NhoVariant.Plain)
            {
                Ensure(389.2);
                TextBox(FixedText("T2"), 0, 27, 750, 84);
                TextBox(FixedText("T3"), 0, 117.1, 749, 62.1);
                Text("Customer:", _base, Caption, 50.4, 226.5, 88.5, 23, XStringFormats.BottomRight);
                Picture(_report.CustomerSignature, 146.9, 192.2, 283.3, 57.4, fit: false);
                HLine(Rule, 146.9, 249.6, 283.3);
                Text("Date:", _base, Caption, 466.7, 226.5, 88.5, 23, XStringFormats.BottomRight);
                Text(date, _base, Value, 558.75, 226.5, 137, 23, XStringFormats.Center);
                HLine(Rule, 558.75, 249.5, 137);
                Text("Representative: ", _base, Caption, 25.4, 320.1, 113.5, 23, XStringFormats.BottomRight);
                Picture(_report.Signature, 146.9, 285.7, 283.3, 57.4, fit: false);
                HLine(Rule, 146.9, 343.1, 283.3);
                Text(_report.H("InspectorName"), _small, Value, 146.9, 343.1, 283.3, 20.9, XStringFormats.Center);
                Text(_report.H("InspectorPhone"), _small, Value, 146.9, 362.1, 283.3, 20.9, XStringFormats.Center);
                _y += 389.2;
                return;
            }
            bool second = _report.SecondWalk;
            double band = second ? 376.7 : 463.2;
            Ensure(band);
            Fill(Bar, 0, 0, Width, 24);
            Text("4.", _base, Value, 5, 0, 33.75, 23);
            Text("Signature", _base, Value, 38.75, 0, 657, 23);
            if (second)
            {
                TextBox(FixedText("T7"), 0, 37.8, 750, 45.5);
                TextBox(FixedText("T8"), 0, 95.8, 749, 63.1);
                TextBox(FixedText("T9"), 0, 173.9, 749, 28.7);
            }
            else
            {
                TextBox(FixedText("T5"), 0, 37.3, 750, 84);
                TextBox(FixedText("T6"), 0, 134.9, 749, 97.5);
                TextBox(FixedText("T7"), 0, 244.1, 749, 46.5);
            }
            double buyerY = second ? 219.6 : 308.2, buyerCaption = second ? 254 : 342.6, designeeY = second ? 309.3 : 397.9, designeeDate = second ? 343.7 : 432.2;
            Text("Buyer's Signature:", _base, Caption, 156, buyerCaption, 135.3, 23, XStringFormats.BottomRight);
            Picture(_report.CustomerSignature, 296.5, buyerY, 283.3, 57.4, fit: false);
            HLine(Rule, 296.5, buyerY + 57.4, 283.3);
            Text("Date:", _base, Caption, 606.8, buyerCaption, 42.7, 23, XStringFormats.BottomLeft);
            Text(date, _base, Value, 649.5, buyerCaption, 94.1, 23);
            string designee = (string.IsNullOrWhiteSpace(_settings.BuilderBName) ? "Builder" : _settings.BuilderBName!.Trim()) + " or Contracted Designee:";
            Text(designee, _base, Caption, 9.8, designeeY, 281.6, 57.4, XStringFormats.BottomRight);
            Picture(_report.Signature, 296.5, designeeY, 283.3, 57.4, fit: false);
            HLine(Rule, 296.5, designeeY + 57.4, 283.3);
            Text("Date:", _base, Caption, 609.5, designeeDate, 42.7, 23, XStringFormats.BottomLeft);
            Text(date, _base, Value, 652.2, designeeDate, 94.1, 23);
            _y += band;
        }

        // N6 (FormId 97).
        private void SignaturesBand()
        {
            Ensure(143.5);
            Fill(Bar, 0, 4, Width, 24);
            Text("Signature(s)", _base, Value, 5, 4, 600, 23);
            Text("Inspector: ", _base, Caption, 344.3, 38.1, 113.5, 23, XStringFormats.BottomRight);
            Picture(_report.Signature, 465.75, 38.1, 283.3, 57.4, fit: false);
            HLine(Rule, 465.75, 95.5, 283.3);
            Text(_report.H("InspectorName"), _small, Value, 465.75, 95.5, 283.3, 20.9, XStringFormats.Center);
            Text(_report.H("InspectorPhone"), _small, Value, 465.75, 116.4, 283.3, 20.9, XStringFormats.Center);
            _y += 143.5;
        }

        // N7 + N8.
        private void ActionItemsPage()
        {
            NewPage();
            bool form97 = _report.Variant == NhoVariant.Form97;
            Fill(Bar, 0, 0, Width, 24);
            Text("Superintendent Action Items", _base, Value, 5, 0, 347.9, 23);
            void Row(string caption, string value, double cx, double vx, double vw, double y, bool right)
            {
                Text(caption, _base, Caption, cx, y, right ? vx - cx : 77, 23, right ? XStringFormats.CenterRight : XStringFormats.CenterLeft);
                Text(value, _base, Value, vx, y, vw, 23);
                HLine(Rule, vx, y + 23, vw);
            }
            Row("Customer:", _report.CustomerName, 0, 77, 412.7, 39, false);
            Row("Phone:", _report.CustomerPhone, 493.9, 570.5, 179.5, 39, true);
            Row("Project:", _report.H("Project"), 0, 77.4, 671.7, 62, false);
            Row("Address:", _report.H("AddressCombo"), 0, 77, 412.7, 85, false);
            Row("Lot/Blk:", _report.H("LotBlock"), 493.9, 570.5, 179.5, 85, true);
            Row("Super:", _report.H("Super"), 0, 77, 673, 108, false);
            _y += 152.2;
            bool dfwEast = _report.BuilderAccountId != null && _settings.BuilderDfwEastAccounts.Contains(_report.BuilderAccountId, StringComparer.OrdinalIgnoreCase);
            if (!dfwEast)
            {
                var small = new XFont(ReportFonts.Base, 8);
                string[] headings = form97
                    ? new[] { "Items to Address", "Pass", "Fail", "Date Completed" }
                    : new[] { "Items To Address", "Sub Contractor", "Date Called", "Date Completed" };
                double[] widths = { 528, 74, 74, 74 };
                Ensure(43.75 + 30);
                double x = 0;
                for (int i = 0; i < 4; i++)
                {
                    Fill(Bar, x, 0, widths[i], 43.75);
                    Border(XColors.Black, x, 0, widths[i], 43.75);
                    CenteredText(headings[i], small, XColors.Black, x + 5, 0, widths[i] - 10, 43.75); // padding 5, wraps (RL L35)
                    x += widths[i];
                }
                _y += 43.75;
                var actionSections = _report.Sections.Where(s => s.IsActionSection && s.Items.Any(i => i.ActionItem)).ToList();
                bool several = _report.Variant != NhoVariant.Plain;
                foreach (var section in actionSections)
                {
                    if (several)
                    {
                        var font = new XFont(ReportFonts.Base, 11);
                        double rowH = Math.Max(17, LineU(font) + 4);
                        Ensure(rowH);
                        Fill(Box, 0, 0, Width, rowH);
                        Border(XColors.Black, 0, 0, Width, rowH);
                        Text($"{section.Number}    {section.Name}", font, XColors.Black, 5, 0, 740, rowH);
                        _y += rowH;
                    }
                    foreach (var item in section.Items.Where(i => i.ActionItem))
                    {
                        if (item.FirstOfGroup && item.Line1.Length > 0)
                        {
                            string heading = $"{item.Number} {item.Name}";
                            int dash = heading.IndexOf('-');
                            if (dash > 0) heading = heading.Substring(0, Math.Max(0, dash - 1));
                            var font = new XFont(ReportFonts.Base, 10);
                            double rowH = Math.Max(15, LineU(font) + 4);
                            Ensure(rowH);
                            Fill(Box, 0, 0, Width, rowH);
                            Border(XColors.Black, 0, 0, Width, rowH);
                            Text(heading, font, XColors.Black, 5, 0, 740, rowH);
                            _y += rowH;
                        }
                        string comment = item.Line2.Length > 0 ? item.Line2 : item.Line1;
                        double h = Math.Max(15, Wrap(comment, _nho, 518).Count * LineU(_nho) + 6);
                        Ensure(h);
                        x = 0;
                        for (int i = 0; i < 4; i++) { Border(XColors.Black, x, 0, widths[i], h); x += widths[i]; }
                        WrappedText(comment, _nho, XColors.Black, 5, 0, 518);
                        _y += h;
                    }
                }
            }
            // N8: only plain NHO and FormId 129, and only for builder account group A (owner-supplied list).
            bool groupA = _report.BuilderAccountId != null && _settings.BuilderGroupAAccounts.Contains(_report.BuilderAccountId, StringComparer.OrdinalIgnoreCase);
            if (!form97 && groupA)
            {
                Ensure(100.1);
                Text(FixedText("T4"), _base, XColors.Black, 3.6, 23.9, 631.25, 23);
                Border(XColors.Black, 650, 26, 10, 10);
                Text(" Yes", _base, XColors.Black, 662, 21.9, 51.5, 23);
                Border(XColors.Black, 707, 26, 10, 10);
                Text(" No", _base, XColors.Black, 719, 21.9, 38.5, 23);
                Text("Customer:", _base, Caption, 3.6, 61.8, 73.8, 23, XStringFormats.BottomLeft);
                HLine(Rule, 77.4, 92.4, 393);
                Text("Date:", _base, Caption, 470.9, 61.8, 88.5, 23, XStringFormats.BottomRight);
                HLine(Rule, 559.4, 92.4, 187);
                _y += 100.1;
            }
        }

        // ── City of Kyle page (K1-K2) ──────────────────────────────────────────────────────────────────────────────

        private void RenderKyle()
        {
            var page = _doc.AddPage();
            page.Size = PageSize.Letter;
            using var g = XGraphics.FromPdfPage(page);
            _g = g;
            _y = 0;
            // RL L43 coordinates; captions in the caption colour, values in the value colour with a rule below.
            var bold = new XFont(ReportFonts.Base, 9.75, XFontStyleEx.Bold);
            var big = new XFont(ReportFonts.Base, 12, XFontStyleEx.Bold);
            var small = new XFont(ReportFonts.Base, 8);
            void CaptionCell(string text, double x, double y, double w) => Text(text, _base, Caption, x, y, w, 23);
            void ValueCell(string text, double x, double y, double w) { Text(text, _base, Value, x, y, w, 23); HLine(Rule, x, y + 23, w); }
            void NoticeBox(string text, double y, double h, bool centre)
            {
                Fill(Bar, 24, y, 692.25, h);
                Border(Value, 24, y, 692.25, h);
                if (centre) CenteredText(text, bold, Value, 29, y, 682.25, h);
                else WrappedText(text, bold, Value, 29, y + 2, 682.25);
            }
            Picture(ReportSettings.ReadImage(_settings.CityLogoPath), 7, 5, 160.8, 106, fit: false);
            Text("Building Inspections Department", new XFont(ReportFonts.Base, 11, XFontStyleEx.Bold), Value, 7, 111, 274.9, 23);
            Text(_settings.CityDeptStreet ?? "", _base, Value, 7, 134, 274.9, 23);
            Text(_settings.CityDeptCityStateZip ?? "", _base, Value, 7, 157, 274.9, 23);
            Text(_settings.CityDeptPhone ?? "", _base, Value, 7, 180, 274.9, 23);
            Text("DUCT LEAKAGE AFFIDAVIT", new XFont(ReportFonts.Base, 17, XFontStyleEx.Bold | XFontStyleEx.Underline), Value, 0, 229, 749.1, 27.2, XStringFormats.Center);
            NoticeBox(FixedText("T10"), 306.25, 53.2, centre: false);
            CaptionCell("Permit #: ", 24, 389.6, 77);
            ValueCell("", 101, 389.6, 233.7); // always empty (RL Q-L7)
            CaptionCell("Date: ", 398.7, 389.6, 77);
            ValueCell(_report.DateInspected?.ToString("MMM d, yyyy", EnUs) ?? "", 475.7, 389.6, 240.5);
            CaptionCell("Site Address:", 24, 435.1, 103);
            ValueCell(_report.H("Address"), 127, 435.1, 230.4);
            ValueCell(_report.H("City"), 357.4, 435.1, 358.8);
            Text("Street Name", small, Caption, 142, 458.1, 117.9, 16.75, XStringFormats.CenterRight);
            Text("City", small, Caption, 357.4, 458.1, 71, 16.75, XStringFormats.CenterRight);
            Text("TEST RESULT:  ", big, Value, 24, 538.5, 103, 23);
            // The ticked-box artwork (139, 519) 51.4 × 45.5 is drawn by RED: a square with ✔, fixed, not data.
            Border(Value, 149.5, 526.5, 30, 30);
            Text("✔", new XFont(ReportFonts.Symbols, 16), Value, 144.5, 521.5, 40, 40, XStringFormats.Center);
            Text("PASS", big, Value, 178.9, 538.5, 80, 23);
            NoticeBox(FixedText("T11"), 582.3, 49, centre: false);
            CaptionCell("Technician: ", 24, 686.8, 91.25);
            ValueCell(_report.H("InspectorName"), 115.25, 686.8, 144.7);
            CaptionCell("Technician Signature: ", 305, 686.8, 151.9);
            Picture(_report.Signature, 456.9, 666, 259.4, 43.8, fit: false);
            HLine(Rule, 456.9, 709.8, 259.4);
            CaptionCell("Company Name: ", 24, 729.8, 129.8);
            ValueCell(_settings.CompanyName ?? "", 153.8, 729.8, 180.9);
            CaptionCell("Phone Number: ", 367.7, 729.8, 122.5);
            ValueCell(_settings.CompanyPhone ?? "", 490.2, 729.8, 226);
            NoticeBox(FixedText("T12"), 949.6, 30, centre: true);
            _g = null;
        }

        // ── Attachments (A1) ───────────────────────────────────────────────────────────────────────────────────────

        private void AppendAttachments()
        {
            foreach (var attachment in _report.Attachments.OfType<JObject>())
            {
                if (attachment["ReferenceOnly"]?.Type == JTokenType.Boolean && (bool)attachment["ReferenceOnly"]!) continue;
                byte[] bytes;
                try { bytes = PdfAttachmentService.EmbeddedPdf(attachment); }
                catch (IOException) { continue; }
                // Owner 2026-10-20: ink saved as annotations is drawn into its page, so it prints (PdfMarkup.Flatten).
                bytes = PdfMarkup.Flatten(bytes);
                bool exclude = attachment["CanExcludePages"]?.Type == JTokenType.Boolean && (bool)attachment["CanExcludePages"]!;
                var annotated = (attachment["AnnotatedPages"] as JArray)?.Select(t => (int)t).ToList() ?? new List<int>();
                using var input = PdfReader.Open(new MemoryStream(bytes), PdfDocumentOpenMode.Import);
                var pages = exclude ? annotated.Where(i => i >= 0 && i < input.PageCount) : Enumerable.Range(0, input.PageCount);
                foreach (int index in pages)
                    _doc.AddPage(input.Pages[index]);
            }
        }

        public void Dispose()
        {
            _g?.Dispose();
            _doc.Dispose();
        }
    }
}
