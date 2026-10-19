using System;
using System.Collections.Generic;
using System.IO;
using PdfSharp.Fonts;

namespace InspectionEditor.Services.Reports
{
    /// <summary>
    /// F-022 LA2 (owner Q-51 default): PDFsharp reads the installed Windows font files. Century Gothic is used only when it
    /// is installed (it is not embedded beyond what PDFsharp's subsetting does for any font); otherwise Arial stands in.
    /// Verdana (NHO text) and Segoe UI Symbol (the ✔/❌ glyphs) likewise fall back to Arial.
    /// </summary>
    public sealed class ReportFonts : IFontResolver
    {
        public const string Base = "Century Gothic";
        public const string Nho = "Verdana";
        public const string Symbols = "Segoe UI Symbol";
        private static readonly object Gate = new();
        private static bool _installed;

        private static readonly Dictionary<string, string[]> Files = new(StringComparer.OrdinalIgnoreCase)
        {
            // regular, bold, italic, bold italic
            ["Century Gothic"] = new[] { "GOTHIC.TTF", "GOTHICB.TTF", "GOTHICI.TTF", "GOTHICBI.TTF" },
            ["Verdana"] = new[] { "verdana.ttf", "verdanab.ttf", "verdanai.ttf", "verdanaz.ttf" },
            ["Segoe UI Symbol"] = new[] { "seguisym.ttf", "", "", "" },
            ["Arial"] = new[] { "arial.ttf", "arialbd.ttf", "ariali.ttf", "arialbi.ttf" }
        };

        public static void Install()
        {
            lock (Gate)
            {
                if (_installed) return;
                if (GlobalFontSettings.FontResolver == null)
                    GlobalFontSettings.FontResolver = new ReportFonts();
                _installed = true;
            }
        }

        private static string FontsFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Fonts");

        private static string? FileFor(string family, int style)
        {
            if (!Files.TryGetValue(family, out var names) || names[style].Length == 0) return null;
            string path = Path.Combine(FontsFolder, names[style]);
            return File.Exists(path) ? path : null;
        }

        public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic)
        {
            int style = (isBold ? 1 : 0) + (isItalic ? 2 : 0);
            // The real face first, then the family's regular face with simulated bold/italic, then Arial the same way.
            foreach (var (family, found) in new[] { (familyName, style), (familyName, 0), ("Arial", style), ("Arial", 0) })
            {
                string? path = FileFor(family, found);
                if (path != null) return new FontResolverInfo(path, isBold && (found & 1) == 0, isItalic && (found & 2) == 0);
            }
            return null;
        }

        public byte[]? GetFont(string faceName) => File.Exists(faceName) ? File.ReadAllBytes(faceName) : null;

        // Whether the real family is installed (the report says which font it used).
        public static bool IsInstalled(string family) => FileFor(family, 0) != null;
    }
}
