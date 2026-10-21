using System;
using System.Diagnostics;

namespace InspectionEditor.Services
{
    /// <summary>
    /// F-017 A1, changed by the owner 2026-10-20 (tablet test of 2.3.4: a plan opened in Edge): a RED working copy (never a
    /// shared original) opens in the device's default PDF program, whatever INSPECT's PDF app setting says. RED's monitor
    /// stays the change detector.
    /// </summary>
    public static class PdfAppLauncher
    {
        // progId and exists are kept for callers and tests; the default program is always used.
        public static ProcessStartInfo StartInfoFor(string workingPath, string? progId = null, Func<string, bool>? exists = null) =>
            new() { FileName = workingPath, UseShellExecute = true };
    }
}
