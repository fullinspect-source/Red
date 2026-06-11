using System;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace InspectionEditor.Services
{
    public class StatsUpdateResult
    {
        public string CurrentDate { get; set; } = "unknown";
        public string LatestDate  { get; set; } = "unknown";
        public bool   Updated     { get; set; }
        public string? Error      { get; set; }
    }

    /// <summary>
    /// Auto-updates data files (quick_comments.json, inspector_stats.json) from cloud URLs.
    /// Checks on app startup and downloads fresh copies if available.
    /// </summary>
    public class DataUpdateService
    {
        private static readonly HttpClient _httpClient = new HttpClient 
        { 
            Timeout = TimeSpan.FromSeconds(15) // Allow time for Dropbox redirect + download
        };
        
        // Dropbox public links (dl=1 for direct download)
        private const string QUICK_COMMENTS_URL   = "https://www.dropbox.com/scl/fi/c5z0aca4981lztxpcik8n/quick_comments.json?rlkey=hnmmlyk5ewdqg4bn87b02ww5l&dl=1";
        // Personal inspector deviation stats (blind spots, strengths per inspector)
        // Also contains builder/project/global averages — one file covers both personal and team stats
        private const string INSPECTOR_STATS_URL  = "https://www.dropbox.com/scl/fi/ami3sunqzzs9day7c5r37/inspector_stats.json?rlkey=mkyrgw8b371rsfubqik8okar8&dl=1";
        private const string INSPECTION_TYPES_URL =
            "https://docs.google.com/spreadsheets/d/1tuT8L7OFWzebwsJ0qe9tegxwier-knASgWpp70qJbqY/export?format=csv";

        // Local paths (in app directory)
        // For single-file publish, BaseDirectory is a temp folder. Use the actual exe location instead.
        private static readonly string AppFolder = Path.GetDirectoryName(Environment.ProcessPath) ?? AppDomain.CurrentDomain.BaseDirectory;
        private static readonly string QuickCommentsPath  = Path.Combine(AppFolder, "quick_comments.json");
        private static readonly string InspectorStatsPath = Path.Combine(AppFolder, "inspector_stats.json");
        private static readonly string InspectionTypesPath = Path.Combine(AppFolder, "inspection_types.csv");
        
        // Only check for updates every 12 hours (opened/closed many times per day)
        private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(12);
        private static readonly string LastCheckFile = Path.Combine(AppFolder, ".last_data_update");
        
        // Warn if data files are older than this (weekly cron = max 7 days; 14 gives one missed-week buffer)
        private static readonly TimeSpan StaleDataThreshold = TimeSpan.FromDays(14);

        /// <summary>
        /// Check if data files are stale (older than 10 days).
        /// Returns warning message if stale, null if OK.
        /// </summary>
        public static string? CheckForStaleData()
        {
            try
            {
                bool quickCommentsStale = IsFileStale(QuickCommentsPath);
                bool inspectorStatsStale = IsFileStale(InspectorStatsPath, useGeneratedDate: true);
                bool inspectionTypesStale = IsFileStale(InspectionTypesPath);

                if (quickCommentsStale || inspectorStatsStale || inspectionTypesStale)
                {
                    var staleFiles = new System.Collections.Generic.List<string>();
                    if (quickCommentsStale) staleFiles.Add("Quick Comments");
                    if (inspectorStatsStale) staleFiles.Add("Inspector Stats");
                    if (inspectionTypesStale) staleFiles.Add("Inspection Types");

                    return $"⚠️ Your {string.Join(" and ", staleFiles)} data is more than 14 days old.\n\n" +
                           "Please connect to the internet and restart RED to get the latest datasets.";
                }
            }
            catch
            {
                // Silently fail
            }
            return null;
        }

        private static bool IsFileStale(string filePath, bool useGeneratedDate = false)
        {
            if (!File.Exists(filePath))
                return true; // Missing file counts as stale

            // Inspector stats should use its embedded generated date because the data itself is time-based.
            // Quick comments and inspection types should use last successful refresh time: those files can be
            // unchanged for weeks while still being the current published dataset.
            if (useGeneratedDate)
            {
                try
                {
                    var content = File.ReadAllText(filePath);
                    var match = System.Text.RegularExpressions.Regex.Match(content, "\"generated\":\\s*\"([^\"]+)\"");
                    if (match.Success && DateTime.TryParse(match.Groups[1].Value, out var generatedDate))
                    {
                        return (DateTime.Now - generatedDate) > StaleDataThreshold;
                    }
                }
                catch { }
            }

            // Fall back to filesystem date
            var fileAge = DateTime.Now - File.GetLastWriteTime(filePath);
            return fileAge > StaleDataThreshold;
        }

        /// <summary>
        /// Check for and download updated data files. Call on app startup.
        /// </summary>
        public static async Task CheckForUpdatesAsync()
        {
            try
            {
                // Skip if we checked recently — UNLESS data files are stale
                bool dataIsStale = IsFileStale(QuickCommentsPath) || IsFileStale(InspectorStatsPath, useGeneratedDate: true) || IsFileStale(InspectionTypesPath);
                if (!dataIsStale && File.Exists(LastCheckFile))
                {
                    var lastCheck = File.GetLastWriteTime(LastCheckFile);
                    if (DateTime.Now - lastCheck < UpdateCheckInterval)
                        return;
                }

                // Update all files in parallel
                await Task.WhenAll(
                    DownloadIfNewerAsync(QUICK_COMMENTS_URL, QuickCommentsPath),
                    DownloadIfNewerAsync(INSPECTOR_STATS_URL, InspectorStatsPath),
                    DownloadIfNewerAsync(INSPECTION_TYPES_URL, InspectionTypesPath,
                        content => content.TrimStart().StartsWith("INS Type"))
                );

                // Mark check time
                File.WriteAllText(LastCheckFile, DateTime.Now.ToString("o"));
            }
            catch
            {
                // Silently fail - updates are optional enhancement
            }
        }

        /// <summary>Returns the "generated" date from the local stats file, formatted for display.</summary>
        public static string GetLocalStatsDate()
        {
            try
            {
                if (!File.Exists(InspectorStatsPath)) return "not installed";
                var content = File.ReadAllText(InspectorStatsPath);
                var m = Regex.Match(content, "\"generated\":\\s*\"([^\"]+)\"");
                if (m.Success && DateTime.TryParse(m.Groups[1].Value, out var dt))
                    return dt.ToString("MMM d, yyyy");
            }
            catch { }
            return "unknown";
        }

        /// <summary>
        /// Force-downloads the latest stats (bypasses 12 h throttle) and returns
        /// what was found versus what the user already had.
        /// </summary>
        public static async Task<StatsUpdateResult> ForceUpdateStatsAsync()
        {
            var result = new StatsUpdateResult { CurrentDate = GetLocalStatsDate() };
            try
            {
                var response = await _httpClient.GetAsync(INSPECTOR_STATS_URL);
                if (!response.IsSuccessStatusCode)
                {
                    result.Error = $"Server error {(int)response.StatusCode}";
                    result.LatestDate = result.CurrentDate;
                    return result;
                }

                var remote = (await response.Content.ReadAsStringAsync()).Trim();
                if (!remote.StartsWith("{") && !remote.StartsWith("["))
                {
                    result.Error = "Invalid data received";
                    result.LatestDate = result.CurrentDate;
                    return result;
                }

                // Extract generated date from remote content
                var m = Regex.Match(remote, "\"generated\":\\s*\"([^\"]+)\"");
                result.LatestDate = (m.Success && DateTime.TryParse(m.Groups[1].Value, out var dt))
                    ? dt.ToString("MMM d, yyyy") : "unknown";

                // Always refresh companion datasets on a forced update, even if stats did not change.
                // If their content is identical, DownloadIfNewerAsync touches the files so the stale warning
                // reflects the successful refresh instead of the embedded/generated data age.
                await DownloadIfNewerAsync(QUICK_COMMENTS_URL, QuickCommentsPath);
                await DownloadIfNewerAsync(INSPECTION_TYPES_URL, InspectionTypesPath,
                    content => content.TrimStart().StartsWith("INS Type"));

                // Check if stats content actually changed
                if (File.Exists(InspectorStatsPath) && File.ReadAllText(InspectorStatsPath) == remote)
                {
                    File.SetLastWriteTime(InspectorStatsPath, DateTime.Now);
                    File.WriteAllText(LastCheckFile, DateTime.Now.ToString("o"));
                    result.Updated = false;
                    return result;
                }

                // Save and mark check time
                File.WriteAllText(InspectorStatsPath, remote);
                File.WriteAllText(LastCheckFile, DateTime.Now.ToString("o"));
                result.Updated = true;
            }
            catch (Exception ex)
            {
                result.Error = ex.Message.Length > 50 ? ex.Message[..50] + "…" : ex.Message;
                result.LatestDate = result.CurrentDate;
            }
            return result;
        }

        /// <summary>Returns the "generated" date from the local team stats file, formatted for display.
        /// Team averages (by builder, by project) are embedded in inspector_stats.json.</summary>
        public static string GetLocalTeamStatsDate() => GetLocalStatsDate();

        private static async Task DownloadIfNewerAsync(
            string url,
            string localPath,
            Func<string, bool>? isValid = null)
        {
            if (url.Contains("PLACEHOLDER"))
                return;

            isValid ??= content => content.StartsWith("{") || content.StartsWith("[");

            try
            {
                var response = await _httpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode)
                    return;

                var remoteContent = await response.Content.ReadAsStringAsync();
                remoteContent = remoteContent.Trim();
                if (!isValid(remoteContent))
                    return;

                if (File.Exists(localPath))
                {
                    var localContent = File.ReadAllText(localPath);
                    if (localContent == remoteContent)
                    {
                        File.SetLastWriteTime(localPath, DateTime.Now);
                        return;
                    }
                }

                File.WriteAllText(localPath, remoteContent);
                System.Diagnostics.Debug.WriteLine($"Updated {Path.GetFileName(localPath)} from cloud");
            }
            catch
            {
                // Silently fail for individual file
            }
        }
    }
}
