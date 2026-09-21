using System;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
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
            Timeout = TimeSpan.FromSeconds(30) // UpdateNetworkService also bounds response bodies.
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
        // Download mutable data to AppData. The install folder may be read-only, and a bundled
        // quick_comments.json must never shadow a newer downloaded copy.
        private static readonly string QuickCommentsPath  = Path.Combine(AppIdentity.LocalAppDataPath, "quick_comments.json");
        private static readonly string InspectorStatsPath = Path.Combine(AppFolder, "inspector_stats.json");
        private static readonly string InspectionTypesPath = Path.Combine(AppFolder, "inspection_types.csv");
        
        // Only check for updates every 12 hours (opened/closed many times per day)
        private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(12);
        private static readonly string LastCheckFile = Path.Combine(AppIdentity.LocalAppDataPath, ".last_data_update");
        
        // Warn if data files are older than this (weekly cron = max 7 days; 14 gives one missed-week buffer)
        private static readonly TimeSpan StaleDataThreshold = TimeSpan.FromDays(14);

        /// <summary>
        /// Check if data files are stale (older than 14 days).
        /// Returns warning message if stale, null if OK.
        /// </summary>
        public static string? CheckForStaleData()
        {
            try
            {
                bool quickCommentsStale = IsFileStale(QuickCommentsPath, useGeneratedDate: true);
                bool inspectorStatsStale = IsFileStale(InspectorStatsPath, useGeneratedDate: true);
                bool inspectionTypesStale = IsFileStale(InspectionTypesPath);

                if (quickCommentsStale || inspectorStatsStale || inspectionTypesStale)
                {
                    var staleFiles = new System.Collections.Generic.List<string>();
                    if (quickCommentsStale) staleFiles.Add("Quick Comments");
                    if (inspectorStatsStale) staleFiles.Add("Inspector Stats");
                    if (inspectionTypesStale) staleFiles.Add("Inspection Types");

                    return $"Your {string.Join(" and ", staleFiles)} data is more than 14 days old.\n\n" +
                           "RED tried to refresh the datasets at startup but they are still stale. " +
                           "Open About and triple-click RED to retry the dataset refresh.";
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

            // Generated JSON datasets use their embedded timestamp. Other files use their local refresh time.
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
                Directory.CreateDirectory(AppIdentity.LocalAppDataPath);
                bool dataIsStale = IsFileStale(QuickCommentsPath, useGeneratedDate: true) || IsFileStale(InspectorStatsPath, useGeneratedDate: true) || IsFileStale(InspectionTypesPath);
                if (!dataIsStale && File.Exists(LastCheckFile))
                {
                    var lastCheck = File.GetLastWriteTime(LastCheckFile);
                    if (DateTime.Now - lastCheck < UpdateCheckInterval)
                        return;
                }

                // Update all files in parallel
                var refreshed = await Task.WhenAll(
                    DownloadIfNewerAsync(QUICK_COMMENTS_URL, QuickCommentsPath,
                        IsValidQuickCommentsPayload, preserveNewerGeneratedData: true),
                    DownloadIfNewerAsync(INSPECTOR_STATS_URL, InspectorStatsPath,
                        IsValidStatsPayload, preserveNewerGeneratedData: true),
                    DownloadIfNewerAsync(INSPECTION_TYPES_URL, InspectionTypesPath,
                        content => content.TrimStart().StartsWith("INS Type"))
                );

                // A failed dataset must not be hidden behind the 12-hour success throttle.
                if (Array.TrueForAll(refreshed, succeeded => succeeded))
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

        public static string GetLocalQuickCommentsDate() => GetGeneratedDateDisplay(QuickCommentsPath);

        public static string GetLocalInspectionTypesDate() => GetFileDateDisplay(InspectionTypesPath);

        public static string GetLastDataCheckDate() => GetFileDateDisplay(LastCheckFile, includeTime: true);

        public static bool IsLocalQuickCommentsStale() => IsFileStale(QuickCommentsPath, useGeneratedDate: true);

        public static bool IsLocalStatsStale() => IsFileStale(InspectorStatsPath, useGeneratedDate: true);

        public static bool IsLocalInspectionTypesStale() => IsFileStale(InspectionTypesPath);

        private static string GetGeneratedDateDisplay(string path)
        {
            try
            {
                if (!File.Exists(path)) return "not installed";
                var generated = GetGeneratedDate(File.ReadAllText(path));
                return generated.HasValue ? generated.Value.ToString("MMM d, yyyy") : "unknown";
            }
            catch { return "unknown"; }
        }

        private static string GetFileDateDisplay(string path, bool includeTime = false)
        {
            try
            {
                if (!File.Exists(path)) return "never";
                var timestamp = File.GetLastWriteTime(path);
                return timestamp.ToString(includeTime ? "MMM d, yyyy h:mm tt" : "MMM d, yyyy");
            }
            catch { return "unknown"; }
        }

        /// <summary>
        /// Force-downloads the latest stats (bypasses 12 h throttle) and returns
        /// what was found versus what the user already had.
        /// </summary>
        public static async Task<StatsUpdateResult> ForceUpdateStatsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = new StatsUpdateResult { CurrentDate = GetLocalStatsDate() };
            // Companion failures must not prevent stats refresh, or claim the stats server is offline.
            var quickTask = DownloadIfNewerAsync(QUICK_COMMENTS_URL, QuickCommentsPath,
                IsValidQuickCommentsPayload, preserveNewerGeneratedData: true, cancellationToken: cancellationToken);
            var typesTask = DownloadIfNewerAsync(INSPECTION_TYPES_URL, InspectionTypesPath,
                content => content.TrimStart().StartsWith("INS Type"), cancellationToken: cancellationToken);
            bool statsSucceeded = false;
            try
            {
                string remote = (await UpdateNetworkService.GetStringAsync(_httpClient, INSPECTOR_STATS_URL, cancellationToken)).Trim();
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsValidStatsPayload(remote)) throw new InvalidDataException("Invalid stats dataset.");
                string? before = File.Exists(InspectorStatsPath) ? File.ReadAllText(InspectorStatsPath) : null;
                StoreValidatedData(InspectorStatsPath, remote, preserveNewerGeneratedData: true);
                result.LatestDate = GetLocalStatsDate();
                result.Updated = before != File.ReadAllText(InspectorStatsPath);
                statsSucceeded = true;
            }
            catch (Exception ex)
            {
                result.Error = ex is InvalidDataException
                    ? "Inspector stats refresh returned invalid data. Existing stats were kept; retry from About."
                    : UpdateNetworkService.DescribeFailure(ex, "refresh inspector stats");
                result.LatestDate = result.CurrentDate;
            }
            bool[] companions = await Task.WhenAll(quickTask, typesTask);
            cancellationToken.ThrowIfCancellationRequested();
            if (statsSucceeded && Array.TrueForAll(companions, succeeded => succeeded))
            {
                try
                {
                    Directory.CreateDirectory(AppIdentity.LocalAppDataPath);
                    File.WriteAllText(LastCheckFile, DateTime.Now.ToString("o"));
                }
                catch { /* Check-time metadata must not disguise a successful stats refresh. */ }
            }
            else if (statsSucceeded)
                result.Error = "Inspector stats refreshed. Quick Comments or Inspection Types could not refresh; retry from About. Existing data was retained.";
            return result;
        }

        /// <summary>Returns the "generated" date from the local team stats file, formatted for display.
        /// Team averages (by builder, by project) are embedded in inspector_stats.json.</summary>
        public static string GetLocalTeamStatsDate() => GetLocalStatsDate();

        internal static async Task<bool> DownloadIfNewerAsync(
            string url,
            string localPath,
            Func<string, bool>? isValid = null,
            bool preserveNewerGeneratedData = false,
            CancellationToken cancellationToken = default)
        {
            if (url.Contains("PLACEHOLDER")) return false;
            isValid ??= content => content.StartsWith("{") || content.StartsWith("[");
            try
            {
                string separator = url.Contains('?') ? "&" : "?";
                string requestUrl = $"{url}{separator}_={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}";
                string remoteContent = (await UpdateNetworkService.GetStringAsync(_httpClient, requestUrl, cancellationToken)).Trim();
                cancellationToken.ThrowIfCancellationRequested();
                if (!isValid(remoteContent)) return false;
                StoreValidatedData(localPath, remoteContent, preserveNewerGeneratedData);
                return true;
            }
            catch
            {
                // The caller records success only if all datasets actually refreshed.
                return false;
            }
        }

        internal static void StoreValidatedData(string localPath, string remoteContent, bool preserveNewerGeneratedData)
        {
            if (File.Exists(localPath))
            {
                string localContent = File.ReadAllText(localPath);
                if (preserveNewerGeneratedData)
                {
                    var remoteGenerated = GetGeneratedDate(remoteContent);
                    var localGenerated = GetGeneratedDate(localContent);
                    if (remoteGenerated.HasValue && localGenerated.HasValue && remoteGenerated.Value < localGenerated.Value)
                        return;
                }
                if (localContent == remoteContent)
                {
                    File.SetLastWriteTime(localPath, DateTime.Now);
                    return;
                }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(localPath) ?? AppIdentity.LocalAppDataPath);
            string temporaryPath = localPath + "." + Guid.NewGuid().ToString("N") + ".download";
            try
            {
                File.WriteAllText(temporaryPath, remoteContent);
                File.Move(temporaryPath, localPath, true);
            }
            finally { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
        }

        internal static bool IsValidStatsPayload(string content)
        {
            try
            {
                var data = Newtonsoft.Json.Linq.JObject.Parse(content);
                return GetGeneratedDate(content).HasValue && data["inspectors"] is Newtonsoft.Json.Linq.JObject;
            }
            catch { return false; }
        }

        private static bool IsValidQuickCommentsPayload(string content)
        {
            return content.StartsWith("{") &&
                   Regex.IsMatch(content, "\"generated\"\\s*:\\s*\"[^\"]+\"") &&
                   Regex.IsMatch(content, "\"items\"\\s*:\\s*\\{");
        }

        private static DateTime? GetGeneratedDate(string content)
        {
            try
            {
                // Only the dataset's own timestamp counts, never a nested inspector field.
                var token = Newtonsoft.Json.Linq.JObject.Parse(content)["generated"];
                if (token?.Type is Newtonsoft.Json.Linq.JTokenType.String or Newtonsoft.Json.Linq.JTokenType.Date &&
                    DateTime.TryParse(token.ToString(), out var generated)) return generated;
            }
            catch { }
            return null;
        }
    }
}

