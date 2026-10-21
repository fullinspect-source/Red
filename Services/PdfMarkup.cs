using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace InspectionEditor.Services
{
    /// <summary>
    /// Owner tablet test of 2.3.4 (2026-10-20): a plan marked up in the PDF app printed every page and none of the ink.
    /// <list type="bullet">
    /// <item>ChangedPages: the pages the inspector changed in RED's working copy compared with what it started from (page
    /// drawing or annotations), so plans that arrive with annotations on every page do not print in full.</item>
    /// <item>Flatten: draws each visible annotation's appearance into its page, so ink saved as annotations (Edge, most PDF
    /// apps) is on the printed page whatever the page copier or renderer does with annotations.</item>
    /// </list>
    /// </summary>
    public static class PdfMarkup
    {
        // Sorted 0-based indices of the pages of 'after' that differ from the same page of 'before' (or are new). Null when
        // either PDF cannot be read (the caller then keeps INSPECT's any-annotation rule).
        public static int[]? ChangedPages(byte[] before, byte[] after)
        {
            try
            {
                var old = Fingerprints(before);
                var now = Fingerprints(after);
                return Enumerable.Range(0, now.Count).Where(i => i >= old.Count || old[i] != now[i]).ToArray();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return null;
            }
        }

        private static List<string> Fingerprints(byte[] pdf)
        {
            using var input = new MemoryStream(pdf, writable: false);
            using var document = PdfReader.Open(input, PdfDocumentOpenMode.Import);
            var list = new List<string>();
            foreach (PdfPage page in document.Pages)
            {
                using var sha = SHA256.Create();
                var text = new StringBuilder();
                foreach (var item in page.Contents.Elements)
                {
                    if (Resolve(item) is PdfDictionary { Stream: not null } stream)
                        text.Append(Convert.ToHexString(sha.ComputeHash(Decoded(stream)))).Append(';');
                }
                // Annotations by what they look like and say, not by object numbers or dates a PDF app may rewrite.
                if (page.Elements.GetArray("/Annots") is PdfArray annots)
                    foreach (var item in annots.Elements)
                        if (Resolve(item) is PdfDictionary annot)
                        {
                            var rect = annot.Elements.GetRectangle("/Rect");
                            text.Append(annot.Elements.GetName("/Subtype")).Append(' ')
                                .Append(Math.Round(rect.X1, 1).ToString(CultureInfo.InvariantCulture)).Append(',')
                                .Append(Math.Round(rect.Y1, 1).ToString(CultureInfo.InvariantCulture)).Append(',')
                                .Append(Math.Round(rect.X2, 1).ToString(CultureInfo.InvariantCulture)).Append(',')
                                .Append(Math.Round(rect.Y2, 1).ToString(CultureInfo.InvariantCulture)).Append(' ')
                                .Append(annot.Elements.GetString("/Contents")).Append(' ')
                                .Append(AppearanceHash(annot)).Append('|');
                        }
                list.Add(text.ToString());
            }
            return list;
        }

        private static string AppearanceHash(PdfDictionary annot)
        {
            if (Appearance(annot) is not PdfDictionary { Stream: not null } form) return "";
            using var sha = SHA256.Create();
            return Convert.ToHexString(sha.ComputeHash(Decoded(form)));
        }

        // A copy of 'pdf' with every visible annotation that has a normal appearance drawn into its page and removed from
        // the page's annotation list. Links and pop-ups are left alone. The original bytes are returned when anything fails.
        public static byte[] Flatten(byte[] pdf)
        {
            try
            {
                using var input = new MemoryStream(pdf, writable: false);
                using var document = PdfReader.Open(input, PdfDocumentOpenMode.Modify);
                bool changed = false;
                int pageNumber = 0;
                foreach (PdfPage page in document.Pages)
                {
                    pageNumber++;
                    if (page.Elements.GetArray("/Annots") is not PdfArray annots || annots.Elements.Count == 0) continue;
                    var drawn = new List<PdfItem>();
                    var operators = new StringBuilder();
                    int n = 0;
                    foreach (var item in annots.Elements)
                    {
                        if (Resolve(item) is not PdfDictionary annot) continue;
                        string subtype = annot.Elements.GetName("/Subtype");
                        if (subtype is "/Link" or "/Popup") continue;
                        int flags = annot.Elements.GetInteger("/F");
                        if ((flags & 2) != 0 || (flags & 32) != 0) continue; // Hidden, NoView
                        if (Appearance(annot) is not PdfDictionary { Stream: not null } form || form.Reference == null) continue;
                        var rect = annot.Elements.GetRectangle("/Rect");
                        var box = form.Elements.GetRectangle("/BBox");
                        var (bx1, by1, bx2, by2) = Transformed(box, form.Elements.GetArray("/Matrix"));
                        if (bx2 - bx1 < 0.01 || by2 - by1 < 0.01 || rect.Width < 0.01 || rect.Height < 0.01) continue;
                        double sx = rect.Width / (bx2 - bx1), sy = rect.Height / (by2 - by1);
                        double tx = Math.Min(rect.X1, rect.X2) - bx1 * sx, ty = Math.Min(rect.Y1, rect.Y2) - by1 * sy;
                        if (form.Elements.GetName("/Subtype") != "/Form") form.Elements.SetName("/Subtype", "/Form");
                        string name = $"/RedAnnot{pageNumber}x{++n}";
                        var xobjects = page.Resources.Elements.GetDictionary("/XObject");
                        if (xobjects == null)
                        {
                            xobjects = new PdfDictionary(document);
                            page.Resources.Elements["/XObject"] = xobjects;
                        }
                        xobjects.Elements[name] = form.Reference;
                        operators.Append("q ").Append(F(sx)).Append(" 0 0 ").Append(F(sy)).Append(' ').Append(F(tx)).Append(' ').Append(F(ty))
                                 .Append(" cm ").Append(name).Append(" Do Q\n");
                        drawn.Add(item);
                    }
                    if (drawn.Count == 0) continue;
                    // The page's own drawing is wrapped in q/Q so its graphics state cannot leak into the appearances.
                    page.Contents.PrependContent().CreateStream(Encoding.ASCII.GetBytes("q\n"));
                    page.Contents.AppendContent().CreateStream(Encoding.ASCII.GetBytes("Q\n" + operators));
                    foreach (var item in drawn) annots.Elements.Remove(item);
                    if (annots.Elements.Count == 0) page.Elements.Remove("/Annots");
                    changed = true;
                }
                if (!changed) return pdf;
                using var output = new MemoryStream();
                document.Save(output);
                return output.ToArray();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return pdf;
            }
        }

        private static PdfDictionary? Appearance(PdfDictionary annot)
        {
            if (annot.Elements.GetDictionary("/AP") is not PdfDictionary ap) return null;
            var normal = Resolve(ap.Elements["/N"]) as PdfDictionary;
            if (normal == null || normal.Stream != null) return normal;
            // A state dictionary (tick boxes, radio buttons): the appearance named by /AS.
            string state = annot.Elements.GetName("/AS");
            return state.Length > 0 ? Resolve(normal.Elements[state]) as PdfDictionary : null;
        }

        private static (double, double, double, double) Transformed(PdfRectangle box, PdfArray? matrix)
        {
            double a = 1, b = 0, c = 0, d = 1, e = 0, f = 0;
            if (matrix is { Elements.Count: 6 })
            {
                a = matrix.Elements.GetReal(0); b = matrix.Elements.GetReal(1); c = matrix.Elements.GetReal(2);
                d = matrix.Elements.GetReal(3); e = matrix.Elements.GetReal(4); f = matrix.Elements.GetReal(5);
            }
            var xs = new List<double>(); var ys = new List<double>();
            foreach (var (x, y) in new[] { (box.X1, box.Y1), (box.X2, box.Y1), (box.X1, box.Y2), (box.X2, box.Y2) })
            {
                xs.Add(a * x + c * y + e);
                ys.Add(b * x + d * y + f);
            }
            return (xs.Min(), ys.Min(), xs.Max(), ys.Max());
        }

        private static byte[] Decoded(PdfDictionary stream)
        {
            try { return stream.Stream.UnfilteredValue; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return stream.Stream.Value; }
        }

        private static PdfItem? Resolve(PdfItem? item) => item is PdfReference reference ? reference.Value : item;

        private static string F(double value) => value.ToString("0.#####", CultureInfo.InvariantCulture);
    }
}
