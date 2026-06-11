using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace InspectionEditor.Services
{
    /// <summary>
    /// Service to load and query inspector deviation stats from the weekly-generated JSON.
    /// Shows indicators (😃/↓/↓↓/↓↓↓) for items where inspector differs from team norms.
    /// </summary>
    public class InspectorStatsService
    {
        private InspectorStatsData? _statsData;
        private DateTime _lastLoadTime = DateTime.MinValue;
        private readonly TimeSpan _cacheExpiry = TimeSpan.FromHours(24);
        
        // Known paths to check for stats file (in priority order)
        private static readonly string[] StatsFilePaths = new[]
        {
            // Same folder as executable (use ProcessPath for single-file publish)
            Path.Combine(Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory, "inspector_stats.json"),
            Path.Combine(AppContext.BaseDirectory, "inspector_stats.json"),
            // Local Dropbox
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), 
                "Library", "CloudStorage", "Dropbox", "P", "inspector_stats.json"),
            // Direct Dropbox path
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), 
                "Dropbox", "P", "inspector_stats.json"),
            // App data folder (for distributed copies)
            Path.Combine(AppIdentity.LocalAppDataPath, "inspector_stats.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), 
                AppIdentity.LegacyAppDataFolderName, "inspector_stats.json"),
            // Current directory (fallback)
            "inspector_stats.json"
        };

        // Minimum team rate threshold - items below this are too rare to be meaningful blind spots
        // Note: TeamRate is stored as percentage (e.g., 3.0 = 3%), not decimal (0.03)
        private const double MIN_TEAM_RATE_THRESHOLD = 3.0; // 3%

        /// <summary>
        /// Get the deviation indicator for a specific item.
        /// </summary>
        /// <param name="inspectorName">Full inspector name (e.g., "Trent Fuller")</param>
        /// <param name="appCode">Inspection code (e.g., "IER", "PRER")</param>
        /// <param name="itemNumber">Item number (e.g., "3.1", "4.7")</param>
        /// <returns>Indicator string (😃, ↓, ↓↓, ↓↓↓, ⚪) or empty if no significant deviation</returns>
        public string GetIndicator(string? inspectorName, string? appCode, string? itemNumber)
        {
            if (string.IsNullOrWhiteSpace(inspectorName) || 
                string.IsNullOrWhiteSpace(appCode) || 
                string.IsNullOrWhiteSpace(itemNumber))
                return "";

            EnsureStatsLoaded();
            
            if (_statsData?.Inspectors == null)
                return "";

            ItemStats? foundItem = null;

            // Try exact match first
            if (_statsData.Inspectors.TryGetValue(inspectorName, out var inspector))
            {
                foundItem = inspector.Items?.FirstOrDefault(i => 
                    string.Equals(i.App, appCode, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(i.Item, itemNumber, StringComparison.OrdinalIgnoreCase));
            }

            // Try case-insensitive match if not found
            if (foundItem == null)
            {
                var matchingInspector = _statsData.Inspectors
                    .FirstOrDefault(kvp => string.Equals(kvp.Key, inspectorName, StringComparison.OrdinalIgnoreCase));
                
                foundItem = matchingInspector.Value?.Items?.FirstOrDefault(i => 
                    string.Equals(i.App, appCode, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(i.Item, itemNumber, StringComparison.OrdinalIgnoreCase));
            }

            if (foundItem == null)
                return "";

            string indicator = foundItem.Indicator ?? "";
            
            // If it's a blind spot indicator (↓, ↓↓, ↓↓↓) but team rate is too low,
            // show gray circle instead - the item is too rare to be a meaningful blind spot
            if (indicator.Contains("↓") && foundItem.TeamRate <= MIN_TEAM_RATE_THRESHOLD)
            {
                return "⚪"; // Gray circle for insignificant items
            }

            return indicator;
        }

        /// <summary>
        /// Get detailed stats for a specific item (for tooltips).
        /// </summary>
        public ItemStats? GetItemStats(string? inspectorName, string? appCode, string? itemNumber)
        {
            if (string.IsNullOrWhiteSpace(inspectorName) || 
                string.IsNullOrWhiteSpace(appCode) || 
                string.IsNullOrWhiteSpace(itemNumber))
                return null;

            EnsureStatsLoaded();
            
            if (_statsData?.Inspectors == null)
                return null;

            // Try case-insensitive match
            var matchingInspector = _statsData.Inspectors
                .FirstOrDefault(kvp => string.Equals(kvp.Key, inspectorName, StringComparison.OrdinalIgnoreCase));
            
            return matchingInspector.Value?.Items?.FirstOrDefault(i => 
                string.Equals(i.App, appCode, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(i.Item, itemNumber, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Get summary stats for an inspector.
        /// </summary>
        public InspectorSummary? GetInspectorSummary(string? inspectorName)
        {
            if (string.IsNullOrWhiteSpace(inspectorName))
                return null;

            EnsureStatsLoaded();
            
            if (_statsData?.Inspectors == null)
                return null;

            var matchingInspector = _statsData.Inspectors
                .FirstOrDefault(kvp => string.Equals(kvp.Key, inspectorName, StringComparison.OrdinalIgnoreCase));
            
            return matchingInspector.Value?.Summary;
        }

        /// <summary>
        /// Get the team average fail count per inspection for a specific app + section.
        /// Returns null if no data available.
        /// </summary>
        public double? GetSectionAverage(string? appCode, string? sectionNumber)
        {
            if (string.IsNullOrWhiteSpace(appCode) || string.IsNullOrWhiteSpace(sectionNumber))
                return null;

            EnsureStatsLoaded();
            
            if (_statsData?.SectionAverages == null)
                return null;

            // Try exact match first
            Dictionary<string, double>? secs = null;
            if (_statsData.SectionAverages.TryGetValue(appCode, out secs))
            {
                // exact match found
            }
            else
            {
                // Case-insensitive fallback
                var match = _statsData.SectionAverages
                    .FirstOrDefault(kvp => string.Equals(kvp.Key, appCode, StringComparison.OrdinalIgnoreCase));
                secs = match.Value; // null if not found
            }

            if (secs != null && secs.TryGetValue(sectionNumber, out var avg))
                return avg;

            return null;
        }

        /// <summary>
        /// Get section average for a specific builder client.
        /// </summary>
        public double? GetSectionAverageByBuilder(string? appCode, string? sectionNumber, string? builderName)
        {
            if (string.IsNullOrWhiteSpace(appCode) || string.IsNullOrWhiteSpace(sectionNumber) || string.IsNullOrWhiteSpace(builderName))
                return null;

            EnsureStatsLoaded();
            if (_statsData?.SectionAveragesByBuilder == null)
                return null;

            // Find app (case-insensitive)
            var appMatch = _statsData.SectionAveragesByBuilder
                .FirstOrDefault(kvp => string.Equals(kvp.Key, appCode, StringComparison.OrdinalIgnoreCase));
            if (appMatch.Value == null) return null;

            // Find builder (case-insensitive)
            var builderMatch = appMatch.Value
                .FirstOrDefault(kvp => string.Equals(kvp.Key, builderName, StringComparison.OrdinalIgnoreCase));
            if (builderMatch.Value == null) return null;

            if (builderMatch.Value.TryGetValue(sectionNumber, out var avg))
                return avg;
            return null;
        }

        /// <summary>
        /// Get section average for a specific project/neighborhood.
        /// Supports fragmented → canonical lookup via project_aliases map.
        /// </summary>
        public double? GetSectionAverageByProject(string? appCode, string? sectionNumber, string? projectName)
        {
            if (string.IsNullOrWhiteSpace(appCode) || string.IsNullOrWhiteSpace(sectionNumber) || string.IsNullOrWhiteSpace(projectName))
                return null;

            EnsureStatsLoaded();
            if (_statsData?.SectionAveragesByProject == null)
                return null;

            // Try direct canonical match first
            var appMatch = _statsData.SectionAveragesByProject
                .FirstOrDefault(kvp => string.Equals(kvp.Key, appCode, StringComparison.OrdinalIgnoreCase));
            if (appMatch.Value == null) return null;

            var projectMatch = appMatch.Value
                .FirstOrDefault(kvp => string.Equals(kvp.Key, projectName, StringComparison.OrdinalIgnoreCase));
            if (projectMatch.Value != null && projectMatch.Value.TryGetValue(sectionNumber, out var avg))
                return avg;

            return null;
        }

        /// <summary>
        /// Get inspection average for a specific project/neighborhood.
        /// </summary>
        public double? GetInspectionAverageByProject(string? appCode, string? projectName)
        {
            if (string.IsNullOrWhiteSpace(appCode) || string.IsNullOrWhiteSpace(projectName))
                return null;

            EnsureStatsLoaded();
            if (_statsData?.SectionAveragesByProject == null)
                return null;

            var appMatch = _statsData.SectionAveragesByProject
                .FirstOrDefault(kvp => string.Equals(kvp.Key, appCode, StringComparison.OrdinalIgnoreCase));
            if (appMatch.Value == null) return null;

            var projectMatch = appMatch.Value
                .FirstOrDefault(kvp => string.Equals(kvp.Key, projectName, StringComparison.OrdinalIgnoreCase));
            if (projectMatch.Value == null) return null;

            return Math.Round(projectMatch.Value.Values.Sum(), 1);
        }

        /// <summary>
        /// Get the total team average fail count per inspection for an entire app (sum of all sections).
        /// Returns null if no data available.
        /// </summary>
        public double? GetInspectionAverage(string? appCode)
        {
            if (string.IsNullOrWhiteSpace(appCode))
                return null;

            EnsureStatsLoaded();

            if (_statsData?.SectionAverages == null)
                return null;

            Dictionary<string, double>? secs = null;
            if (_statsData.SectionAverages.TryGetValue(appCode, out secs))
            {
                // exact match
            }
            else
            {
                var match = _statsData.SectionAverages
                    .FirstOrDefault(kvp => string.Equals(kvp.Key, appCode, StringComparison.OrdinalIgnoreCase));
                secs = match.Value;
            }

            if (secs == null || secs.Count == 0)
                return null;

            return Math.Round(secs.Values.Sum(), 1);
        }

        /// <summary>
        /// Get total inspection average for a specific builder.
        /// </summary>
        public double? GetInspectionAverageByBuilder(string? appCode, string? builderName)
        {
            if (string.IsNullOrWhiteSpace(appCode) || string.IsNullOrWhiteSpace(builderName))
                return null;

            EnsureStatsLoaded();
            if (_statsData?.SectionAveragesByBuilder == null)
                return null;

            var appMatch = _statsData.SectionAveragesByBuilder
                .FirstOrDefault(kvp => string.Equals(kvp.Key, appCode, StringComparison.OrdinalIgnoreCase));
            if (appMatch.Value == null) return null;

            var builderMatch = appMatch.Value
                .FirstOrDefault(kvp => string.Equals(kvp.Key, builderName, StringComparison.OrdinalIgnoreCase));
            if (builderMatch.Value == null) return null;

            return Math.Round(builderMatch.Value.Values.Sum(), 1);
        }

        /// <summary>
        /// Check if stats data is available.
        /// </summary>
        public bool HasStats()
        {
            EnsureStatsLoaded();
            return _statsData?.Inspectors != null && _statsData.Inspectors.Count > 0;
        }

        /// <summary>
        /// Get the generation timestamp of the stats file.
        /// </summary>
        public DateTime? GetStatsGeneratedTime()
        {
            EnsureStatsLoaded();
            if (_statsData?.Generated != null && DateTime.TryParse(_statsData.Generated, out var dt))
                return dt;
            return null;
        }

        /// <summary>
        /// Force reload of stats from disk.
        /// </summary>
        public void Refresh()
        {
            _lastLoadTime = DateTime.MinValue;
            EnsureStatsLoaded();
        }

        /// <summary>
        /// Get diagnostic info for troubleshooting stats loading issues.
        /// </summary>
        public string GetDiagnostics(string? inspectorName, string? appCode)
        {
            EnsureStatsLoaded();
            var lines = new System.Collections.Generic.List<string>();
            
            // Show which file was loaded
            string? loadedPath = StatsFilePaths.FirstOrDefault(File.Exists);
            lines.Add($"Stats file: {loadedPath ?? "NOT FOUND"}");
            
            // Show all checked paths
            foreach (var p in StatsFilePaths)
                lines.Add($"  {(File.Exists(p) ? "✓" : "✗")} {p}");
            
            if (_statsData == null)
            {
                lines.Add("Stats data: NULL (failed to load or no file)");
                return string.Join("\n", lines);
            }
            
            lines.Add($"Generated: {_statsData.Generated ?? "unknown"}");
            lines.Add($"Inspectors: {_statsData.Inspectors?.Count ?? 0}");
            
            if (!string.IsNullOrWhiteSpace(inspectorName))
            {
                var match = _statsData.Inspectors?
                    .FirstOrDefault(kvp => string.Equals(kvp.Key, inspectorName, StringComparison.OrdinalIgnoreCase));
                
                if (match?.Value != null)
                {
                    var items = match.Value.Value.Items ?? new System.Collections.Generic.List<ItemStats>();
                    lines.Add($"Inspector '{inspectorName}' found as '{match.Value.Key}': {items.Count} total items");
                    
                    if (!string.IsNullOrWhiteSpace(appCode))
                    {
                        var appItems = items.Where(i => 
                            string.Equals(i.App, appCode, StringComparison.OrdinalIgnoreCase)).ToList();
                        lines.Add($"  {appCode} items: {appItems.Count}");
                        foreach (var ai in appItems.Take(3))
                            lines.Add($"    {ai.Item}: dev={ai.Deviation:+0.0;-0.0} ind={ai.Indicator}");
                    }
                }
                else
                {
                    lines.Add($"Inspector '{inspectorName}' NOT FOUND in stats!");
                    if (_statsData.Inspectors != null)
                    {
                        // Show closest matches
                        var similar = _statsData.Inspectors.Keys
                            .Where(k => k.IndexOf(inspectorName?.Split(' ')[0] ?? "", StringComparison.OrdinalIgnoreCase) >= 0)
                            .Take(3);
                        foreach (var s in similar)
                            lines.Add($"  Similar: '{s}'");
                    }
                }
            }
            
            return string.Join("\n", lines);
        }

        public List<ItemStats> GetItemsByTeamRate(string? appCode, int maxItems = 15, double minTeamRate = 5.0)
        {
            if (string.IsNullOrWhiteSpace(appCode)) return new List<ItemStats>();
            EnsureStatsLoaded();
            if (_statsData?.Inspectors == null) return new List<ItemStats>();
            var byItem = new Dictionary<string, ItemStats>(StringComparer.OrdinalIgnoreCase);
            foreach (var inspector in _statsData.Inspectors.Values)
            {
                foreach (var item in inspector.Items ?? Enumerable.Empty<ItemStats>())
                {
                    if (!string.Equals(item.App, appCode, StringComparison.OrdinalIgnoreCase)) continue;
                    if (string.IsNullOrWhiteSpace(item.Item)) continue;
                    // Skip section 1 — administrative checkoffs, not real inspection findings
                    if (item.Item.StartsWith("1.") || item.Item == "1") continue;
                    if (!byItem.TryGetValue(item.Item, out var existing) || item.TeamRate > existing.TeamRate)
                        byItem[item.Item] = item;
                }
            }
            return byItem.Values
                .Where(i => i.TeamRate >= minTeamRate)
                .OrderByDescending(i => i.TeamRate)
                .Take(maxItems)
                .ToList();
        }

        private void EnsureStatsLoaded()
        {
            // Check cache
            if (_statsData != null && (DateTime.Now - _lastLoadTime) < _cacheExpiry)
                return;

            // Find the stats file
            string? statsPath = StatsFilePaths.FirstOrDefault(File.Exists);
            
            if (statsPath == null)
            {
                _statsData = null;
                return;
            }

            try
            {
                string json = File.ReadAllText(statsPath);
                _statsData = JsonConvert.DeserializeObject<InspectorStatsData>(json);
                _lastLoadTime = DateTime.Now;
            }
            catch (Exception)
            {
                // Silently fail - stats are optional enhancement
                _statsData = null;
            }
        }
    }

    #region Data Models

    public class InspectorStatsData
    {
        [JsonProperty("generated")]
        public string? Generated { get; set; }

        [JsonProperty("version")]
        public string? Version { get; set; }

        [JsonProperty("inspectors")]
        public Dictionary<string, InspectorData>? Inspectors { get; set; }

        [JsonProperty("section_averages")]
        public Dictionary<string, Dictionary<string, double>>? SectionAverages { get; set; }

        [JsonProperty("section_averages_by_builder")]
        public Dictionary<string, Dictionary<string, Dictionary<string, double>>>? SectionAveragesByBuilder { get; set; }

        [JsonProperty("section_averages_by_project")]
        public Dictionary<string, Dictionary<string, Dictionary<string, double>>>? SectionAveragesByProject { get; set; }
    }

    public class InspectorData
    {
        [JsonProperty("items")]
        public List<ItemStats>? Items { get; set; }

        [JsonProperty("summary")]
        public InspectorSummary? Summary { get; set; }
    }

    public class ItemStats
    {
        [JsonProperty("app")]
        public string? App { get; set; }

        [JsonProperty("item")]
        public string? Item { get; set; }

        [JsonProperty("description")]
        public string? Description { get; set; }

        [JsonProperty("indicator")]
        public string? Indicator { get; set; }

        [JsonProperty("deviation")]
        public double Deviation { get; set; }

        [JsonProperty("inspector_rate")]
        public double InspectorRate { get; set; }

        [JsonProperty("team_rate")]
        public double TeamRate { get; set; }

        [JsonProperty("inspector_catches")]
        public int InspectorCatches { get; set; }

        [JsonProperty("inspector_inspections")]
        public int InspectorInspections { get; set; }
    }

    public class InspectorSummary
    {
        [JsonProperty("total_flags")]
        public int TotalFlags { get; set; }

        [JsonProperty("blind_spots")]
        public int BlindSpots { get; set; }

        [JsonProperty("strengths")]
        public int Strengths { get; set; }

        [JsonProperty("worst_blind_spots")]
        public List<ItemStats>? WorstBlindSpots { get; set; }

        [JsonProperty("best_strengths")]
        public List<ItemStats>? BestStrengths { get; set; }
    }

    #endregion
}
