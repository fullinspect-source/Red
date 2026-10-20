using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace InspectionEditor.Services
{
    /// <summary>
    /// Owner 2026-10-19: the My List row tick boxes. Remove, Move to My List and Approve act on the ticked rows that the
    /// list shows (a search can hide rows), in file-name order like the old pick list. Ticks are kept by full path, so
    /// they survive a reload of the same folder and are dropped for files that are gone.
    /// </summary>
    public static class TickedRowsService
    {
        public static List<T> Chosen<T>(IEnumerable<T> shownRows, Func<T, bool> isTicked, Func<T, string> fileName) =>
            shownRows.Where(isTicked).OrderBy(fileName, StringComparer.OrdinalIgnoreCase).ToList();

        public static string NothingTicked(string verb) =>
            $"Tick the boxes at the left of the rows to {verb}, then press the button again.";

        public const string StillLoading = "The list is still loading. Press the button again in a moment.";

        public static void Set(HashSet<string> ticked, string path, bool isTicked)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (isTicked) ticked.Add(path);
            else ticked.Remove(path);
        }

        /// <summary>
        /// A row counts only when its path is ticked and its file is in the folder the list now holds. During a tab
        /// switch's load, or after a failed load, the old folder's rows (still ticked on screen) are not acted on.
        /// </summary>
        public static bool Counts(string path, ISet<string> ticked, string? listedFolder) =>
            ticked.Contains(path) && SameFolder(Path.GetDirectoryName(path), listedFolder);

        public static bool SameFolder(string? a, string? b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            try { return string.Equals(Path.GetFullPath(a).TrimEnd('\\'), Path.GetFullPath(b).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
            catch (Exception) { return false; }
        }

        public static void Prune(HashSet<string> ticked, IEnumerable<string> listedPaths)
        {
            var listed = new HashSet<string>(listedPaths, StringComparer.OrdinalIgnoreCase);
            ticked.RemoveWhere(path => !listed.Contains(path));
        }
    }
}
