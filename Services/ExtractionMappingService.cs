using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace InspectionEditor.Services
{
    internal sealed class ExtractionMapping
    {
        public string Source { get; init; } = "";
        public string InspectionCode { get; init; } = "";
        public string SectionMatch { get; init; } = "";
        public string PromptMatch { get; init; } = "";
        public string FieldKey { get; init; } = "";
        public string Label { get; init; } = "";
        public string CompareRule { get; init; } = "";
        public string LegacyItemNumber { get; init; } = "";
        public int Priority { get; init; }
        public bool IsBuiltIn { get; init; }
    }

    internal static class ExtractionMappingService
    {
        private static readonly object Sync = new();
        private static List<ExtractionMapping>? _mappings;

        public static ExtractionMapping? Resolve(
            string source,
            string? inspectionCode,
            string? sectionNumber,
            string? sectionName,
            string? prompt,
            string? legacyItemNumber)
        {
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(prompt))
                return null;

            var rows = GetMappings();
            string normalizedSource = NormalizeToken(source);
            string normalizedCode = EnergyComplianceService.NormalizeCode(inspectionCode);
            string sectionText = BuildSectionText(sectionNumber, sectionName);
            string promptText = NormalizeText(prompt);
            string legacy = NormalizeItemNumber(legacyItemNumber);

            return rows
                .Where(row => SourceMatches(row, normalizedSource))
                .Where(row => InspectionCodeMatches(row, normalizedCode))
                .Select(row => new { Row = row, Score = Score(row, sectionText, promptText, legacy) })
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Row.Priority)
                .ThenBy(x => x.Row.IsBuiltIn)
                .FirstOrDefault()?.Row;
        }

        private static List<ExtractionMapping> GetMappings()
        {
            lock (Sync)
            {
                if (_mappings != null)
                    return _mappings;

                var rows = new List<ExtractionMapping>();
                foreach (string path in CandidatePaths().Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!File.Exists(path))
                        continue;

                    try
                    {
                        rows.AddRange(ReadCsv(path));
                    }
                    catch
                    {
                        // Mapping is an enhancement, not a load blocker. Built-ins still apply.
                    }
                }

                rows.AddRange(BuiltInMappings());
                _mappings = rows;
                return _mappings;
            }
        }

        private static IEnumerable<string> CandidatePaths()
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(local))
                yield return Path.Combine(local, "RED", "ExtractionMapping.csv");

            yield return Path.Combine(AppContext.BaseDirectory, "ExtractionMapping.csv");
            yield return Path.Combine(AppContext.BaseDirectory, "data", "ExtractionMapping.csv");
            yield return Path.Combine(AppContext.BaseDirectory, "docs", "extraction_field_mapping.csv");
        }

        private static IEnumerable<ExtractionMapping> ReadCsv(string path)
        {
            var lines = File.ReadAllLines(path);
            if (lines.Length == 0)
                yield break;

            var headers = ParseCsvLine(lines[0])
                .Select((name, index) => new { Name = NormalizeHeader(name), Index = index })
                .ToDictionary(x => x.Name, x => x.Index, StringComparer.OrdinalIgnoreCase);

            for (int i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i]))
                    continue;

                var cells = ParseCsvLine(lines[i]);
                string Get(params string[] names)
                {
                    foreach (string name in names)
                    {
                        if (headers.TryGetValue(NormalizeHeader(name), out int index) && index >= 0 && index < cells.Count)
                            return cells[index]?.Trim() ?? "";
                    }
                    return "";
                }

                string source = Get("Source");
                string field = Get("FieldKey", "ExtractedDataField");
                if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(field))
                    continue;

                int.TryParse(Get("Priority"), out int priority);

                yield return new ExtractionMapping
                {
                    Source = source,
                    InspectionCode = Get("InspectionCode"),
                    SectionMatch = Get("SectionMatch", "Section", "SectionLabel"),
                    PromptMatch = Get("PromptMatch", "ChecklistPrompt", "PromptKeyProposal", "CurrentDisplayLabel"),
                    FieldKey = field,
                    Label = Get("HudLabel", "Label", "CurrentDisplayLabel"),
                    CompareRule = Get("CompareRule", "ComparisonRule"),
                    LegacyItemNumber = Get("LegacyItemNumber", "ItemNumber"),
                    Priority = priority
                };
            }
        }

        private static IReadOnlyList<string> ParseCsvLine(string line)
        {
            var cells = new List<string>();
            var sb = new StringBuilder();
            bool quoted = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '"')
                {
                    if (quoted && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        sb.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = !quoted;
                    }
                }
                else if (c == ',' && !quoted)
                {
                    cells.Add(sb.ToString());
                    sb.Clear();
                }
                else
                {
                    sb.Append(c);
                }
            }

            cells.Add(sb.ToString());
            return cells;
        }

        private static IEnumerable<ExtractionMapping> BuiltInMappings()
        {
            ExtractionMapping Row(string source, string codes, string section, string prompt, string field, string label, string compare, int priority = 10) =>
                new()
                {
                    Source = source,
                    InspectionCode = codes,
                    SectionMatch = section,
                    PromptMatch = prompt,
                    FieldKey = field,
                    Label = label,
                    CompareRule = compare,
                    Priority = priority,
                    IsBuiltIn = true
                };

            const string allEc = "IER/HER/IEF/HEF/HET/AFI/PLY/ACI/PS";

            yield return Row("EC", allEc, "", "u-factor OR window u-factor", "WindowUFactor", "U-factor", "ExactOrClose");
            yield return Row("EC", allEc, "", "shgc", "WindowSHGC", "SHGC", "ExactOrClose");
            yield return Row("EC", allEc, "", "wall r-value OR wood frame wall r-value", "WallR", "Wall", "AtLeast");
            yield return Row("EC", allEc, "", "sloped ceiling r-value OR sloped ceiling insulation", "SlopedCeilingR", "Sloped Ceiling", "AtLeast");
            yield return Row("EC", allEc, "", "attic ceiling r-value OR ceiling r-value", "AtticCeilingR", "Attic Ceiling", "AtLeast");
            yield return Row("EC", allEc, "", "attic wall r-value", "AtticWallR", "Attic Wall", "AtLeast");
            yield return Row("EC", allEc, "", "attic roof r-value OR roof deck r-value", "AtticRoofR", "Attic Roof", "AtLeast");
            yield return Row("EC", allEc, "", "supply duct r-value", "SupplyDuctR", "Supply Duct", "AtLeast");
            yield return Row("EC", allEc, "", "return duct r-value", "ReturnDuctR", "Return Duct", "AtLeast");
            yield return Row("EC", allEc, "", "hot water pipe r-value OR pipe insulation", "HotWaterPipeR", "Pipe", "AtLeast");
            yield return Row("EC", allEc, "", "conditioned floor area OR floor area", "ConditionedFloorArea", "Floor Area", "ExactOrClose");
            yield return Row("EC", allEc, "", "conditioned volume OR volume", "ConditionedVolume", "Volume", "ExactOrClose");
            yield return Row("EC", allEc, "", "bedrooms OR number of bedrooms", "NumberOfBedrooms", "Bedrooms", "ExactOrClose");
            yield return Row("EC", allEc, "", "returns OR number of returns", "NumberOfReturns", "Returns", "ExactOrClose");
            yield return Row("EC", allEc, "", "fresh air OR ventilation cfm OR cfm target", "TargetFreshAirCfm", "Fresh Air", "AtLeast");
            yield return Row("EC", allEc, "mechanical vent", "cfm target", "TargetFreshAirCfm", "Fresh Air", "AtLeast", 25);
            yield return Row("EC", allEc, "mechanical vent", "runtime target OR run time target", "TargetRunTime", "Run Time", "TextMatch", 25);
            yield return Row("EC", allEc, "blower door", "blower door target OR cfm target", "BlowerDoorMaxCfm", "Blower Door", "AtMost", 25);
            yield return Row("EC", allEc, "duct system", "duct leakage OR duct system #1 total leakage OR duct system #2 total leakage OR leakage cfm", "EffectiveDuctLeakageCfm", "Duct Leakage", "AtMost", 20);
            yield return Row("EC", allEc, "", "blower door OR cfm50 OR ach50", "BlowerDoorMaxCfm", "Blower Door", "AtMost");
            yield return Row("EC", allEc, "", "cooling seer OR seer", "HvacCoolingSeer", "SEER", "AtLeast");
            yield return Row("EC", allEc, "", "tonnage", "HvacTonnage", "Tonnage", "ExactOrClose");
            yield return Row("EC", allEc, "", "design airflow", "DesignAirflowCfm", "Design Airflow", "ExactOrClose");
            yield return Row("EC", allEc, "", "fan watts", "VentFanWatts", "Fan Watts", "AtMost");
            yield return Row("EC", allEc, "", "water heater fuel", "WaterHeaterFuel", "WH Fuel", "TextMatch");
            yield return Row("EC", allEc, "", "water heater capacity", "WaterHeaterCapacity", "WH Capacity", "TextMatch");
            yield return Row("EC", allEc, "", "iecc code", "IECCVersionYear", "IECC Code", "TextMatch", 15);
            yield return Row("EC", allEc, "", "energy star", "EnergyStarProgram", "Energy Star", "TextMatch", 15);
            yield return Row("EC", allEc, "", "performance path OR performance iecc", "PerformancePath", "Type", "TextMatch");

            yield return Row("SLAB", "CPP", "", "foundation type", "FoundationType", "Foundation", "TextMatch");
            yield return Row("SLAB", "CPP", "", "front to back quantity OR side to side quantity", "CableCountTotal", "Plan total", "CableSum", 20);
            yield return Row("SLAB", "CPP", "", "beam width", "BeamWidthInches", "Beam W", "BeamWidth", 20);
            yield return Row("SLAB", "CPP", "", "bottom of beam OR beam depth OR top of form to bottom of beam", "BeamDepthInches", "TOF-BOB", "BeamDepth", 20);
            yield return Row("SLAB", "CPP", "", "slab thickness OR proper thickness", "SlabThicknessInches", "Slab", "AtLeast");
            yield return Row("SLAB", "CPP", "", "hold-down OR hold down OR holddown", "HolddownCount", "Holddowns", "ExactOrClose");
        }

        private static bool SourceMatches(ExtractionMapping row, string source) =>
            NormalizeToken(row.Source) == source;

        private static bool InspectionCodeMatches(ExtractionMapping row, string code)
        {
            if (string.IsNullOrWhiteSpace(row.InspectionCode) || row.InspectionCode.Trim() == "*")
                return true;

            return SplitAlternatives(row.InspectionCode)
                .Select(EnergyComplianceService.NormalizeCode)
                .Any(c => string.Equals(c, code, StringComparison.OrdinalIgnoreCase));
        }

        private static int Score(ExtractionMapping row, string sectionText, string promptText, string legacyItemNumber)
        {
            int score = 0;

            if (!string.IsNullOrWhiteSpace(row.SectionMatch))
            {
                string sectionNeedle = NormalizeText(row.SectionMatch);
                if (!LooseTextMatches(sectionText, sectionNeedle))
                    return 0;
                score += 80;
            }

            if (!string.IsNullOrWhiteSpace(row.PromptMatch))
            {
                int bestPrompt = SplitAlternatives(row.PromptMatch)
                    .Select(NormalizeText)
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => PromptScore(promptText, x))
                    .DefaultIfEmpty(0)
                    .Max();
                if (bestPrompt == 0)
                    return 0;
                score += bestPrompt;
            }

            string rowLegacy = NormalizeItemNumber(row.LegacyItemNumber);
            if (!string.IsNullOrWhiteSpace(rowLegacy) && rowLegacy == legacyItemNumber)
                score += 15;

            return score;
        }

        private static int PromptScore(string prompt, string needle)
        {
            if (prompt == needle)
                return 120;
            if (LooseTextMatches(prompt, needle))
                return 90;
            return 0;
        }

        private static bool LooseTextMatches(string haystack, string needle)
        {
            if (string.IsNullOrWhiteSpace(haystack) || string.IsNullOrWhiteSpace(needle))
                return false;

            return haystack.Contains(needle, StringComparison.OrdinalIgnoreCase) ||
                   needle.Contains(haystack, StringComparison.OrdinalIgnoreCase);
        }

        private static IEnumerable<string> SplitAlternatives(string value) =>
            Regex.Split(value ?? "", @"\s*(?:\bOR\b|/|\|)\s*", RegexOptions.IgnoreCase)
                .Where(x => !string.IsNullOrWhiteSpace(x));

        private static string BuildSectionText(string? sectionNumber, string? sectionName)
        {
            string combined = $"{sectionNumber ?? ""} {sectionName ?? ""}";
            return NormalizeText(combined);
        }

        internal static string NormalizeFieldKey(string? value) =>
            Regex.Replace(value ?? "", @"[^A-Za-z0-9]", "").ToUpperInvariant();

        private static string NormalizeText(string? value)
        {
            string text = (value ?? "").ToLowerInvariant();
            text = Regex.Replace(text, @"^\s*\d+(?:\.\d+)*\s*[-.)]?\s*", "");
            text = Regex.Replace(text, @"[^a-z0-9]+", " ");
            return Regex.Replace(text, @"\s+", " ").Trim();
        }

        private static string NormalizeToken(string? value) =>
            Regex.Replace(value ?? "", @"[^A-Za-z0-9]", "").ToUpperInvariant();

        private static string NormalizeHeader(string? value) =>
            Regex.Replace(value ?? "", @"[^A-Za-z0-9]", "").ToUpperInvariant();

        private static string NormalizeItemNumber(string? value) =>
            (value ?? "").Trim().ToLowerInvariant();
    }
}
