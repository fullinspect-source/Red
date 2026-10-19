using System;
using System.Diagnostics;
using System.IO;

namespace InspectionEditor.Services
{
    /// <summary>
    /// F-017 A1: opens a RED working copy (never a shared original) in the PDF app chosen in INSPECT's settings, imported as
    /// the profile's PdfAppProgId (F-003): AcrobatDC with "/n", FoxitReader, DrawboardPdf, MicrosoftEdge, or "(default)",
    /// the system .pdf handler (INSPECT's AppList, DOC §6). A listed app that is not installed falls back to the system
    /// handler. RED's monitor stays the change detector.
    /// </summary>
    public static class PdfAppLauncher
    {
        public static ProcessStartInfo StartInfoFor(string workingPath, string? progId = null, Func<string, bool>? exists = null)
        {
            exists ??= File.Exists;
            progId ??= InspectorProfileService.Load()?.PdfAppProgId;
            string? exe = null;
            string arguments = Quote(workingPath);
            switch ((progId ?? "").Trim())
            {
                case "AcrobatDC":
                    exe = FirstExisting(exists,
                        @"%ProgramFiles(x86)%\Adobe\Acrobat Reader DC\Reader\AcroRd32.exe",
                        @"%ProgramFiles%\Adobe\Acrobat Reader DC\Reader\AcroRd32.exe",
                        @"%ProgramFiles%\Adobe\Acrobat DC\Acrobat\Acrobat.exe",
                        @"%ProgramFiles(x86)%\Adobe\Acrobat Reader\Reader\AcroRd32.exe");
                    arguments = "/n " + Quote(workingPath); // INSPECT's Options: a new instance per file
                    break;
                case "FoxitReader":
                    exe = FirstExisting(exists,
                        @"%ProgramFiles(x86)%\Foxit Software\Foxit Reader\FoxitReader.exe",
                        @"%ProgramFiles(x86)%\Foxit Software\Foxit PDF Reader\FoxitPDFReader.exe",
                        @"%ProgramFiles%\Foxit Software\Foxit PDF Reader\FoxitPDFReader.exe");
                    break;
                case "MicrosoftEdge":
                    exe = FirstExisting(exists,
                        @"%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe",
                        @"%ProgramFiles%\Microsoft\Edge\Application\msedge.exe");
                    break;
                // DrawboardPdf has no program path in INSPECT's list; it and "(default)" use the system handler.
            }
            return exe == null
                ? new ProcessStartInfo { FileName = workingPath, UseShellExecute = true }
                : new ProcessStartInfo { FileName = exe, Arguments = arguments, UseShellExecute = false };
        }

        private static string? FirstExisting(Func<string, bool> exists, params string[] candidates)
        {
            foreach (string candidate in candidates)
            {
                string path = Environment.ExpandEnvironmentVariables(candidate);
                if (!path.Contains('%') && exists(path))
                    return path;
            }
            return null;
        }

        private static string Quote(string path) => "\"" + path.Replace("\"", "") + "\"";
    }
}
