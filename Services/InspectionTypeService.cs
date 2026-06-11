using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace InspectionEditor.Services
{
    public class InspectionTypeConfig
    {
        public string InsType { get; set; } = "";
        public string Alias { get; set; } = "";
        public string OfferFileLabel { get; set; } = "";
        public List<string> FreeReinspectionTypes { get; set; } = new();  // col D
        public List<string> ExpirationStageTypes { get; set; } = new();   // col E
        public bool EngineerReview { get; set; }       // col F
        public bool ShowPass { get; set; }             // col G
        public bool ShowComplete { get; set; }         // col H
        public bool ShowCorrectAndProceed { get; set; }// col I
        public bool ShowFailNext { get; set; }         // col J
        public bool ShowFailPO { get; set; }           // col K
    }

    public class InspectionTypeService
    {
        private static readonly string CsvPath = Path.Combine(
            Path.GetDirectoryName(Environment.ProcessPath) ?? AppDomain.CurrentDomain.BaseDirectory,
            "inspection_types.csv");

        private Dictionary<string, InspectionTypeConfig>? _cache;

        public InspectionTypeConfig? GetConfig(string insType)
        {
            _cache ??= LoadAll();
            return _cache.TryGetValue(insType.Trim().ToUpperInvariant(), out var cfg) ? cfg : null;
        }

        // Called after DataUpdateService downloads a fresh CSV to bust the in-memory cache.
        public void InvalidateCache() => _cache = null;

        private Dictionary<string, InspectionTypeConfig> LoadAll()
        {
            var result = new Dictionary<string, InspectionTypeConfig>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(CsvPath)) return result;

            try
            {
                var lines = File.ReadAllLines(CsvPath);
                foreach (var line in lines.Skip(1)) // row 1 is header
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var f = ParseCsvLine(line);
                    if (f.Count < 11) continue;

                    string Get(int i) => i < f.Count ? f[i].Trim() : "";
                    bool GetBool(int i) => Get(i).Equals("Yes", StringComparison.OrdinalIgnoreCase);
                    List<string> GetCodes(int i) => Get(i)
                        .Split(new[] { " or ", ",", "/" }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim().ToUpperInvariant())
                        .Where(s => s.Length > 0)
                        .ToList();

                    var cfg = new InspectionTypeConfig
                    {
                        InsType            = Get(0).ToUpperInvariant(),
                        Alias              = Get(1),
                        OfferFileLabel     = Get(2),
                        FreeReinspectionTypes = GetCodes(3),
                        ExpirationStageTypes  = GetCodes(4),
                        EngineerReview     = GetBool(5),
                        ShowPass           = GetBool(6),
                        ShowComplete       = GetBool(7),
                        ShowCorrectAndProceed = GetBool(8),
                        ShowFailNext       = GetBool(9),
                        ShowFailPO         = GetBool(10),
                    };

                    if (!string.IsNullOrEmpty(cfg.InsType))
                        result[cfg.InsType] = cfg;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"InspectionTypeService load error ({CsvPath}): {ex.Message}");
            }

            return result;
        }

        private static List<string> ParseCsvLine(string line)
        {
            var fields = new List<string>();
            bool inQuotes = false;
            var current = new StringBuilder();

            foreach (char c in line)
            {
                if (c == '"') { inQuotes = !inQuotes; }
                else if (c == ',' && !inQuotes) { fields.Add(current.ToString()); current.Clear(); }
                else { current.Append(c); }
            }
            fields.Add(current.ToString());
            return fields;
        }
    }
}
