using System;

namespace InspectionEditor.Services
{
    internal static class SpecialistFlagService
    {
        internal const string Marker = "\u25A9";

        internal static bool HasFlag(string? comment)
        {
            return !string.IsNullOrEmpty(comment) &&
                   comment.Contains(Marker, StringComparison.Ordinal);
        }

        internal static string RemoveMarker(string? comment)
        {
            return (comment ?? "").Replace(Marker, "").Trim();
        }
    }
}
