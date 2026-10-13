using System;
using System.Text.RegularExpressions;

namespace InspectionEditor.Services
{
    // WPF device-independent units: DPI scales the title and snippet together.
    public static class ChecklistCommentSnippetPolicy
    {
        public static bool HasComment(string? comment) => !string.IsNullOrWhiteSpace(comment);
        public static string Preview(string? comment) => HasComment(comment)
            ? Regex.Replace(comment!.Trim(), @"\s+", " ") : "";
        public static double FontSize(double checklistFontSize) => Math.Clamp(checklistFontSize - 4, 10, 12);
        public static double LineHeight(double checklistFontSize) => FontSize(checklistFontSize) + 2;
    }
}
