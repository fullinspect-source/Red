using System;
using System.Collections.Generic;
using System.Linq;
using InspectionEditor.Models;

namespace InspectionEditor.Services
{
    /// <summary>
    /// Owner 2026-10-24: the editor's checklist search also finds an item when one of its common comments contains the
    /// search text, even if the item's own text does not. The comments are every list RED offers for the item: all its
    /// quick comments (not only the top three), INSPECT's company and personal lists (F-024) and My saved comments.
    /// Matching is the search box's own: case-insensitive "contains".
    /// </summary>
    public enum ChecklistCommentSource { Quick, Saved, Company, InspectUser }

    public sealed record ChecklistComment(ChecklistCommentSource Source, string Text);

    public enum ChecklistSearchMatch { None, Label, CommentOnly }

    /// <summary>
    /// The common comments of one report's items, read once per item (on first use) and kept while the report and its
    /// comment lists are unchanged. <see cref="Stamp"/> names the lists' state; the editor builds a new index when it
    /// differs.
    /// </summary>
    public sealed class ChecklistCommentIndex
    {
        private sealed record Entry(ChecklistComment Comment, string Lower);

        private readonly Func<Item, IEnumerable<ChecklistComment>> _load;
        private readonly Dictionary<string, Entry[]> _byKey = new(StringComparer.Ordinal);

        public ChecklistCommentIndex(Func<Item, IEnumerable<ChecklistComment>> load, string stamp)
        {
            _load = load;
            Stamp = stamp;
        }

        public string Stamp { get; }

        /// <summary>How many distinct items have had their lists read so far.</summary>
        public int LoadedItems => _byKey.Count;

        // A copied item ([+]) shares its number and ItemId with the original, so they share one entry.
        private static string Key(Item item) => $"{item.Number}\u001f{item.ItemId}";

        private Entry[] EntriesFor(Item item)
        {
            string key = Key(item);
            if (!_byKey.TryGetValue(key, out var entries))
            {
                try
                {
                    entries = (_load(item) ?? Enumerable.Empty<ChecklistComment>())
                        .Where(comment => !string.IsNullOrWhiteSpace(comment?.Text))
                        .Select(comment => new Entry(comment, comment.Text.ToLower()))
                        .ToArray();
                }
                catch (Exception)
                {
                    entries = Array.Empty<Entry>(); // a damaged list only loses its comments; the search keeps working
                }
                _byKey[key] = entries;
            }
            return entries;
        }

        public bool HasMatch(Item item, string? filter)
        {
            if (item == null || string.IsNullOrWhiteSpace(filter)) return false;
            string filterLower = filter.ToLower();
            foreach (var entry in EntriesFor(item))
                if (entry.Lower.Contains(filterLower)) return true;
            return false;
        }

        /// <summary>
        /// The item's comments that contain the search text, in list order, each text once (the first list wins). With
        /// <paramref name="source"/>, only that list's comments, before texts are merged across lists.
        /// </summary>
        public IReadOnlyList<ChecklistComment> Matching(Item item, string? filter, ChecklistCommentSource? source = null)
        {
            if (item == null || string.IsNullOrWhiteSpace(filter)) return Array.Empty<ChecklistComment>();
            string filterLower = filter.ToLower();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return EntriesFor(item)
                .Where(entry => source == null || entry.Comment.Source == source)
                .Where(entry => entry.Lower.Contains(filterLower) && seen.Add(entry.Comment.Text.Trim()))
                .Select(entry => entry.Comment)
                .ToList();
        }
    }

    public static class ChecklistCommentSearch
    {
        public const string MarkerText = "🔍 in comments";
        public const string MarkerToolTip = "Shown because one of this item's common comments contains your search";

        /// <summary>
        /// Label: the item's own text matches (the caller's existing rule). CommentOnly: only a common comment matches.
        /// With no search text every item is a Label match, as before.
        /// </summary>
        public static ChecklistSearchMatch Match(bool labelMatches, Item item, string? filter, ChecklistCommentIndex? index)
        {
            if (string.IsNullOrWhiteSpace(filter) || labelMatches) return ChecklistSearchMatch.Label;
            return index?.HasMatch(item, filter) == true ? ChecklistSearchMatch.CommentOnly : ChecklistSearchMatch.None;
        }

        /// <summary>The comments that contain the search text first (in their order), then the rest; none dropped or repeated.</summary>
        public static List<string> MatchingFirst(IEnumerable<string> comments, string? filter)
        {
            var list = (comments ?? Enumerable.Empty<string>()).ToList();
            if (string.IsNullOrWhiteSpace(filter)) return list;
            string filterLower = filter.ToLower();
            return list.Where(c => c?.ToLower().Contains(filterLower) == true)
                .Concat(list.Where(c => c?.ToLower().Contains(filterLower) != true))
                .ToList();
        }

        public static bool Contains(string? comment, string? filter) =>
            !string.IsNullOrWhiteSpace(filter) && comment?.ToLower().Contains(filter.ToLower()) == true;

        /// <summary>Where a matching comment comes from, shown under it in the editor's comment lists.</summary>
        public static string SourceLabel(ChecklistCommentSource source) => source switch
        {
            ChecklistCommentSource.Quick => "Common comment",
            ChecklistCommentSource.Saved => "My saved comment",
            ChecklistCommentSource.Company => "Company comment",
            _ => "My INSPECT comment",
        };
    }
}
