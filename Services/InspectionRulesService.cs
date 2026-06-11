using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace InspectionEditor.Services
{
    public class InspectionRule
    {
        public string ServiceType { get; set; } = "";
        public string ClientName { get; set; } = "";
        public string Inspector { get; set; } = "";
        public string Message { get; set; } = "";
        public bool Active { get; set; }
        public string CreatedBy { get; set; } = "";
        public string CreatedByEmail { get; set; } = "";
        public string CreatedDate { get; set; } = "";
        public string ExpiresDate { get; set; } = "";
        public bool RequireAck { get; set; }
    }

    /// <summary>
    /// Fetches inspection rules from Google Sheets.
    /// Uses the public CSV export URL (sheet must be shared as "Anyone with link can view").
    /// </summary>
    public class InspectionRulesService
    {
        private const string SPREADSHEET_ID = "1E1aHuxRjUm2d7cRYYDaK-7av0BOhoNVLoZeNnQubHcg";
        private const string SHEET_NAME = "inspection_messages.csv";
        
        // CSV export URL for public sheets
        private static string CsvUrl => $"https://docs.google.com/spreadsheets/d/{SPREADSHEET_ID}/gviz/tq?tqx=out:csv&sheet={SHEET_NAME}";
        
        private readonly HttpClient _httpClient;
        private List<InspectionRule> _cachedRules = new List<InspectionRule>();
        private DateTime _lastFetch = DateTime.MinValue;
        
        public InspectionRulesService()
        {
            _httpClient = new HttpClient();
        }

        /// <summary>
        /// Fetches all rules from the spreadsheet (with 5-minute cache).
        /// </summary>
        public async Task<List<InspectionRule>> FetchRulesAsync(bool forceRefresh = false)
        {
            // Use cache if less than 72 hours old
            if (!forceRefresh && _cachedRules.Count > 0 && (DateTime.Now - _lastFetch).TotalHours < 72)
            {
                return _cachedRules;
            }

            try
            {
                var response = await _httpClient.GetStringAsync(CsvUrl);
                _cachedRules = ParseCsv(response);
                _lastFetch = DateTime.Now;
                return _cachedRules;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error fetching rules: {ex.Message}");
                return _cachedRules; // Return cached on error
            }
        }

        /// <summary>
        /// Gets rules that apply to a specific inspection.
        /// Deduplicates rules with identical messages (e.g. same rule across multiple divisions).
        /// </summary>
        public async Task<List<InspectionRule>> GetApplicableRulesAsync(
            string serviceType, 
            string clientName, 
            string inspectorName)
        {
            var allRules = await FetchRulesAsync();
            
            var matched = allRules.Where(r => 
                r.Active &&
                !IsExpired(r.ExpiresDate) &&
                (r.ServiceType.Equals("ALL", StringComparison.OrdinalIgnoreCase) || 
                 r.ServiceType.Equals(serviceType, StringComparison.OrdinalIgnoreCase)) &&
                (r.ClientName.Equals("ALL", StringComparison.OrdinalIgnoreCase) || 
                 clientName.Contains(r.ClientName, StringComparison.OrdinalIgnoreCase) ||
                 r.ClientName.Contains(clientName, StringComparison.OrdinalIgnoreCase)) &&
                (r.Inspector.Equals("ALL", StringComparison.OrdinalIgnoreCase) || 
                 r.Inspector.Equals(inspectorName, StringComparison.OrdinalIgnoreCase))
            ).ToList();

            // Deduplicate rules with identical OR very similar messages.
            // Rules from multiple divisions often have near-identical text with
            // minor regional variations (e.g., "DRH-SOUTH" vs "DRH-NORTHWEST").
            // Group them together and show only one copy.
            var deduplicated = DeduplicateRules(matched);

            return deduplicated;
        }

        /// <summary>
        /// Groups rules that are identical or very similar (>80% word overlap)
        /// and merges them into single entries with combined client names.
        /// </summary>
        private List<InspectionRule> DeduplicateRules(List<InspectionRule> rules)
        {
            var groups = new List<List<InspectionRule>>();

            foreach (var rule in rules)
            {
                bool added = false;
                foreach (var group in groups)
                {
                    // Compare against the first rule in the group
                    if (AreSimilarMessages(group[0].Message, rule.Message))
                    {
                        group.Add(rule);
                        added = true;
                        break;
                    }
                }
                if (!added)
                {
                    groups.Add(new List<InspectionRule> { rule });
                }
            }

            var deduplicated = new List<InspectionRule>();
            foreach (var group in groups)
            {
                var first = group[0];
                if (group.Count > 1)
                {
                    var distinctClients = group
                        .Select(r => r.ClientName.Trim())
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();

                    deduplicated.Add(new InspectionRule
                    {
                        ServiceType = first.ServiceType,
                        ClientName = distinctClients.Count > 1
                            ? string.Join(", ", distinctClients)
                            : first.ClientName,
                        Inspector = first.Inspector,
                        Message = first.Message, // Use the first (longest) version
                        Active = first.Active,
                        CreatedBy = first.CreatedBy,
                        CreatedByEmail = first.CreatedByEmail,
                        CreatedDate = first.CreatedDate,
                        ExpiresDate = first.ExpiresDate,
                        RequireAck = group.Any(r => r.RequireAck)
                    });
                }
                else
                {
                    deduplicated.Add(first);
                }
            }

            return deduplicated;
        }

        /// <summary>
        /// Checks if two messages are similar enough to be considered duplicates.
        /// Uses word-level overlap: if >80% of words are shared, they're duplicates.
        /// </summary>
        private bool AreSimilarMessages(string a, string b)
        {
            if (string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase))
                return true;

            var wordsA = NormalizeToWords(a);
            var wordsB = NormalizeToWords(b);

            if (wordsA.Count == 0 || wordsB.Count == 0)
                return false;

            int shared = wordsA.Intersect(wordsB, StringComparer.OrdinalIgnoreCase).Count();
            int total = Math.Max(wordsA.Count, wordsB.Count);

            double similarity = (double)shared / total;
            return similarity >= 0.80;
        }

        /// <summary>
        /// Normalizes a message to a list of lowercase words (letters/digits only).
        /// </summary>
        private List<string> NormalizeToWords(string text)
        {
            return text
                .Split(new[] { ' ', '\t', '\r', '\n', '.', ',', ':', ';', '(', ')', '/', '-' }, 
                       StringSplitOptions.RemoveEmptyEntries)
                .Select(w => w.Trim().ToLowerInvariant())
                .Where(w => w.Length > 1)
                .ToList();
        }

        private bool IsExpired(string expiresDate)
        {
            if (string.IsNullOrWhiteSpace(expiresDate))
                return false; // No expiration = never expires
            
            if (DateTime.TryParse(expiresDate, out var expires))
            {
                return DateTime.Now > expires;
            }
            return false;
        }

        private List<InspectionRule> ParseCsv(string csv)
        {
            var rules = new List<InspectionRule>();
            var lines = csv.Split('\n').Skip(1); // Skip header row
            
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                
                var fields = ParseCsvLine(line);
                if (fields.Count < 4) continue; // Need at least ServiceType, ClientName, Inspector, Message
                
                // Helper to safely get field or default
                string GetField(int index, string defaultValue = "") =>
                    index < fields.Count ? fields[index].Trim() : defaultValue;
                
                bool GetBoolField(int index, bool defaultValue = true) =>
                    index < fields.Count 
                        ? fields[index].Trim().Equals("TRUE", StringComparison.OrdinalIgnoreCase)
                        : defaultValue;
                
                rules.Add(new InspectionRule
                {
                    ServiceType = GetField(0),
                    ClientName = GetField(1),
                    Inspector = GetField(2),
                    Message = GetField(3),
                    Active = GetBoolField(4, true),           // Default: active
                    CreatedBy = GetField(5),
                    CreatedByEmail = GetField(6),
                    CreatedDate = GetField(7),
                    ExpiresDate = GetField(8),                // Default: no expiration
                    RequireAck = GetBoolField(9, false)       // Default: no ack required
                });
            }
            
            return rules;
        }

        private List<string> ParseCsvLine(string line)
        {
            var fields = new List<string>();
            bool inQuotes = false;
            var current = "";
            
            foreach (char c in line)
            {
                if (c == '"')
                {
                    inQuotes = !inQuotes;
                }
                else if (c == ',' && !inQuotes)
                {
                    fields.Add(current.Trim('"'));
                    current = "";
                }
                else
                {
                    current += c;
                }
            }
            fields.Add(current.Trim('"'));
            
            return fields;
        }
    }
}
