using System;
using System.IO;

namespace InspectionEditor.Services
{
    public sealed record SubmitButtonState(bool Visible, bool Enabled, string Hint);

    public sealed record SignButtonDisplay(string Text, string Background, string Foreground, string Border, string ToolTip);

    /// <summary>
    /// Owner tablet test 2026-10-20 ("could not find how to sign or submit"): what the editor's Submit button and its hint
    /// show (F-013 P2), and the look of the Sign button beside the result button (F-011).
    /// <list type="bullet">
    /// <item>A Review file shows no Submit at all (F-018, owner: no Approve; RED never submits Review files).</item>
    /// <item>Otherwise the button is always there; when it cannot run, the hint beside it says why.</item>
    /// </list>
    /// </summary>
    public static class SubmitAvailability
    {
        public const string SaveFirst = "Submit: save first";
        public const string OnlyFromMyList = "Submit: only from My List";
        public const string Submitting = "Submitting…";

        // The folder a report was opened from, by its parent folder's name (F-013 P2 compares the name too).
        public static bool IsInFolder(string? path, string folderName)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            try { return string.Equals(Path.GetFileName(Path.GetDirectoryName(Path.GetFullPath(path))), folderName, StringComparison.OrdinalIgnoreCase); }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { return false; }
        }

        public static SubmitButtonState For(bool reportOpen, bool hasFile, string? path, bool running)
        {
            if (!reportOpen) return new SubmitButtonState(true, false, "");
            if (IsInFolder(path, "Review")) return new SubmitButtonState(false, false, "");
            if (running) return new SubmitButtonState(true, false, Submitting);
            if (!hasFile || string.IsNullOrWhiteSpace(path)) return new SubmitButtonState(true, false, SaveFirst);
            if (!IsInFolder(path, "MyList")) return new SubmitButtonState(true, false, OnlyFromMyList);
            return new SubmitButtonState(true, true, "");
        }
    }

    public static class SignButtonLook
    {
        public const string Sign = "✍ Sign", Signed = "✍ Signed", NotSigned = "✍ Not signed";

        // Unsigned and editable: red outline, the same red as "Signature required.". Signed: green. Read-only unsigned: grey.
        public static SignButtonDisplay For(bool signed, bool readOnly) =>
            signed
                ? new SignButtonDisplay(Signed, "#DCFCE7", "#166534", "#86EFAC", readOnly ? "Signed. Tap to see the signature." : "Signed. Tap to see or change the signature.")
                : readOnly
                    ? new SignButtonDisplay(NotSigned, "#F1F5F9", "#475569", "#CBD5E1", "Not signed. Tap to see the End section.")
                    : new SignButtonDisplay(Sign, "#FEF2F2", "#B91C1C", "#DC2626", "Tap to sign: opens the End section at the inspector signature.");
    }
}
