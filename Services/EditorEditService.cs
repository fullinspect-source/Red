using System;
using System.Linq;
using InspectionEditor.Models;

namespace InspectionEditor.Services
{
    // UI-independent capture rules. Model identity, not ItemId, owns an edit (duplicates share IDs).
    public static class EditorEditService
    {
        public static bool Owns(InspectionFile? report, Item? item) =>
            item != null && report?.Sections.Any(s => s.Items.Contains(item)) == true;

        public static bool SetComment(InspectionFile? report, Item? item, string text)
        {
            if (!Owns(report, item) || string.Equals(item!.Comments ?? "", text, StringComparison.Ordinal)) return false;
            item!.Comments = text;
            return true;
        }

        public static bool SetValue(InspectionFile? report, Item? item, string text)
        {
            if (!Owns(report, item) || string.Equals(item!.Value?.ToString() ?? "", text, StringComparison.Ordinal)) return false;
            item!.Value = text;
            return true;
        }
    }
}