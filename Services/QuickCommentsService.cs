using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace InspectionEditor.Services
{
    /// <summary>
    /// Service to load and query quick comment suggestions from quick_comments.json.
    /// Comments are pre-polished by AI with [trade] prefixes and professional wording.
    /// </summary>
    public class QuickCommentsService
    {
        private Dictionary<string, List<QuickComment>> _comments = new();
        private DateTime _lastLoadTime = DateTime.MinValue;
        private string? _loadedPath;
        private DateTime _loadedWriteTimeUtc = DateTime.MinValue;
        private static readonly TimeSpan CacheDuration = TimeSpan.FromHours(24);

        private static readonly string[] SearchPaths = new[]
        {
            // Mutable downloaded copy first. A bundled release file is fallback only.
            Path.Combine(AppIdentity.LocalAppDataPath, "quick_comments.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                AppIdentity.LegacyAppDataFolderName, "quick_comments.json"),
            // Same folder as executable (use ProcessPath for single-file publish)
            Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory, "quick_comments.json"),
            Path.Combine(AppContext.BaseDirectory, "quick_comments.json"),
            // Dropbox location (Trent's machine)
            @"C:\Users\trent\Dropbox\P\quick_comments.json",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), 
                "Dropbox", "P", "quick_comments.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), 
                "Library", "CloudStorage", "Dropbox-Personal", "P", "quick_comments.json"),
            // Current directory fallback
            "quick_comments.json"
        };

        public void LoadComments()
        {
            foreach (var path in SearchPaths)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        var writeTimeUtc = File.GetLastWriteTimeUtc(path);
                        if (_comments.Count > 0 &&
                            string.Equals(_loadedPath, path, StringComparison.OrdinalIgnoreCase) &&
                            writeTimeUtc == _loadedWriteTimeUtc &&
                            DateTime.Now - _lastLoadTime < CacheDuration)
                        {
                            return;
                        }

                        var json = File.ReadAllText(path);
                        var data = JsonConvert.DeserializeObject<QuickCommentsData>(json);
                        
                        if (data?.Items != null)
                        {
                            _comments = data.Items;
                            _lastLoadTime = DateTime.Now;
                            _loadedPath = path;
                            _loadedWriteTimeUtc = writeTimeUtc;
                            System.Diagnostics.Debug.WriteLine($"[QuickComments] Loaded {_comments.Count} items from {path}");
                            return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[QuickComments] Error loading from {path}: {ex.Message}");
                }
            }

            System.Diagnostics.Debug.WriteLine("[QuickComments] No quick_comments.json found in any search path");
        }

        /// <summary>
        /// Get quick comment suggestions for a specific app and item number.
        /// </summary>
        /// <param name="app">Inspection type (e.g., "CPP", "IER")</param>
        /// <param name="itemNumber">Item number (e.g., "5.6")</param>
        /// <param name="maxCount">Maximum number of suggestions to return (default 3 for UI, use 10 for Grok exclusions)</param>
        /// <returns>List of suggestion texts, ordered by usage count (descending)</returns>
        public List<string> GetSuggestions(string app, string itemNumber, int maxCount = 3)
        {
            LoadComments();

            var key = $"{app}|{itemNumber}";
            
            if (_comments.TryGetValue(key, out var suggestions))
            {
                return suggestions
                    .OrderByDescending(s => s.Count)
                    .Select(s => s.Text)
                    .Take(maxCount)
                    .ToList();
            }

            return new List<string>();
        }

        /// <summary>
        /// Every suggestion of one inspection type, by item number, most used first, read once (owner 2026-10-24: the
        /// checklist search reads all of them, not only the top three).
        /// </summary>
        public Dictionary<string, List<string>> AllSuggestionsFor(string app)
        {
            LoadComments();
            return SuggestionsByItem(_comments, app);
        }

        public static Dictionary<string, List<string>> SuggestionsByItem(IReadOnlyDictionary<string, List<QuickComment>> comments, string app)
        {
            string prefix = app + "|";
            var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var pair in comments)
            {
                if (!pair.Key.StartsWith(prefix, StringComparison.Ordinal) || pair.Value == null)
                    continue;
                result[pair.Key.Substring(prefix.Length)] = pair.Value
                    .Where(s => s != null && !string.IsNullOrWhiteSpace(s.Text)) // a damaged entry is skipped
                    .OrderByDescending(s => s.Count)
                    .Select(s => s.Text)
                    .ToList();
            }
            return result;
        }

        /// <summary>Names the loaded file's state; it changes when a newer quick_comments.json is read.</summary>
        public string Stamp
        {
            get
            {
                LoadComments();
                return $"{_loadedPath}|{_loadedWriteTimeUtc.Ticks}|{_comments.Count}";
            }
        }

        /// <summary>
        /// Check if there are any suggestions available for an item.
        /// </summary>
        public bool HasSuggestions(string app, string itemNumber)
        {
            LoadComments();
            var key = $"{app}|{itemNumber}";
            return _comments.ContainsKey(key) && _comments[key].Count > 0;
        }
    }

    public class QuickCommentsData
    {
        [JsonProperty("generated")]
        public string? Generated { get; set; }

        [JsonProperty("version")]
        public string? Version { get; set; }

        [JsonProperty("min_usage")]
        public int MinUsage { get; set; }

        [JsonProperty("items")]
        public Dictionary<string, List<QuickComment>>? Items { get; set; }
    }

    public class QuickComment
    {
        [JsonProperty("text")]
        public string Text { get; set; } = "";

        [JsonProperty("count")]
        public int Count { get; set; }
    }
}
