using InspectionEditor.Models;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace InspectionEditor.Services
{
    public sealed class EquipmentAirflowMatch
    {
        public int UnitNumber { get; init; }
        public string OutdoorModel { get; init; } = "";
        public string IndoorModel { get; init; } = "";
        public int AirflowCfm { get; init; }
        public string SourceFile { get; init; } = "";
    }

    /// <summary>
    /// Resolves STRADA's approved HVAC design airflow from the installed
    /// condenser + air-handler matchup recorded in a same-job Energy Final.
    /// It never changes inspection results; it only supplies a design target.
    /// </summary>
    public static class EquipmentAirflowService
    {
        private sealed record AirflowRule(string OutdoorModel, string IndoorModel, int AirflowCfm);

        private static readonly AirflowRule[] Rules =
        {
            new("GZV6SA24", "AHVE24BP13", 773),
            new("GZV6SA30", "AHVE36CP13", 947),
            new("GZV6SA36", "AHVE36CP13", 1140),
            new("GLZS4BA30", "AMST42CU13", 953),
            new("GZV6SA42", "AHVE42CP13", 1367),
        };

        public static IReadOnlyList<EquipmentAirflowMatch> FindMatches(
            string? currentFilePath,
            InspectionFile? currentInspection)
        {
            string jobId = GetJobId(currentFilePath, currentInspection);
            var candidates = new List<(InspectionFile Inspection, string SourceFile)>();

            bool currentIsEnergyFinal = IsEnergyFinal(currentInspection);
            if (currentIsEnergyFinal)
                candidates.Add((currentInspection!, currentFilePath ?? "current Energy Final"));

            // The in-memory Energy Final is authoritative and may contain unsaved edits.
            // Do not scan Dropbox on every keystroke while its model fields are being typed.
            if (!currentIsEnergyFinal)
            {
                foreach (string path in FindSameJobEnergyFinalFiles(currentFilePath, jobId))
                {
                    if (!string.IsNullOrWhiteSpace(currentFilePath) &&
                        string.Equals(Path.GetFullPath(path), Path.GetFullPath(currentFilePath), StringComparison.OrdinalIgnoreCase))
                        continue;

                    try
                    {
                        var inspection = JsonConvert.DeserializeObject<InspectionFile>(File.ReadAllText(path));
                        if (IsEnergyFinal(inspection))
                            candidates.Add((inspection!, path));
                    }
                    catch
                    {
                        // A blocked or incomplete sibling file must not break the active inspection.
                    }
                }
            }

            // The in-memory Energy Final (or the newest/highest-attempt sibling) is
            // authoritative. Never mix unit data across different report attempts.
            var matches = new List<EquipmentAirflowMatch>();
            var candidate = candidates.FirstOrDefault();
            if (candidate.Inspection == null) return matches;

            foreach (var pair in ExtractEquipmentPairs(candidate.Inspection))
            {
                var rule = MatchRule(pair.OutdoorModel, pair.IndoorModel);
                if (rule == null) continue;

                matches.Add(new EquipmentAirflowMatch
                {
                    UnitNumber = pair.UnitNumber,
                    OutdoorModel = pair.OutdoorModel,
                    IndoorModel = pair.IndoorModel,
                    AirflowCfm = rule.AirflowCfm,
                    SourceFile = candidate.SourceFile,
                });
            }

            return matches.OrderBy(m => m.UnitNumber).ToList();
        }

        public static void ApplyMatches(
            EnergyComplianceInfo info,
            string? currentFilePath,
            InspectionFile? currentInspection)
        {
            if (info == null) return;

            // Preserve the normal EC/tonnage target so removing or correcting a model
            // does not leave a stale equipment-match target in the banner.
            if (!string.Equals(info.DesignAirflowSource, "STRADA equipment matchup", StringComparison.Ordinal))
            {
                info.DesignAirflowFallbackCfm ??= info.DesignAirflowCfm;
                info.DesignAirflowFallbackCfm2 ??= info.DesignAirflowCfm2;
                info.DesignAirflowFallbackStatusText ??= info.StatusText;
                info.DesignAirflowFallbackDisplayName ??= info.DisplayName;
            }

            info.DesignAirflowCfm = info.DesignAirflowFallbackCfm;
            info.DesignAirflowCfm2 = info.DesignAirflowFallbackCfm2;
            info.StatusText = info.DesignAirflowFallbackStatusText;
            info.DisplayName = info.DesignAirflowFallbackDisplayName;
            info.DesignAirflowSource = null;
            info.DesignAirflowOutdoorModel = null;
            info.DesignAirflowIndoorModel = null;
            info.DesignAirflowOutdoorModel2 = null;
            info.DesignAirflowIndoorModel2 = null;
            info.DesignAirflowSourceFile = null;

            var matches = FindMatches(currentFilePath, currentInspection);
            foreach (var match in matches)
            {
                if (match.UnitNumber == 1)
                {
                    info.DesignAirflowCfm = match.AirflowCfm.ToString();
                    info.DesignAirflowOutdoorModel = match.OutdoorModel;
                    info.DesignAirflowIndoorModel = match.IndoorModel;
                }
                else if (match.UnitNumber == 2)
                {
                    info.DesignAirflowCfm2 = match.AirflowCfm.ToString();
                    info.DesignAirflowOutdoorModel2 = match.OutdoorModel;
                    info.DesignAirflowIndoorModel2 = match.IndoorModel;
                }
            }

            if (matches.Count > 0)
            {
                info.DesignAirflowSource = "STRADA equipment matchup";
                info.DesignAirflowSourceFile = Path.GetFileName(matches[0].SourceFile);
                info.StatusText = $"STRADA equipment airflow found from {info.DesignAirflowSourceFile}.";
                info.DisplayName ??= info.DesignAirflowSourceFile;
            }
        }

        internal static int? GetAirflowForModels(string? outdoorModel, string? indoorModel)
            => MatchRule(outdoorModel, indoorModel)?.AirflowCfm;

        private static AirflowRule? MatchRule(string? outdoorModel, string? indoorModel)
        {
            string outdoor = NormalizeModel(outdoorModel);
            string indoor = NormalizeModel(indoorModel);
            if (outdoor.Length == 0 || indoor.Length == 0) return null;

            return Rules.FirstOrDefault(rule =>
                outdoor.StartsWith(NormalizeModel(rule.OutdoorModel), StringComparison.Ordinal) &&
                indoor.StartsWith(NormalizeModel(rule.IndoorModel), StringComparison.Ordinal));
        }

        private static string NormalizeModel(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "";
            string normalized = Regex.Replace(value.ToUpperInvariant(), "[^A-Z0-9]", "");
            return normalized is "NI" or "NA" or "NONE" ? "" : normalized;
        }

        private static bool IsEnergyFinal(InspectionFile? inspection)
        {
            string code = inspection?.InspectionCode?.Trim().ToUpperInvariant() ?? "";
            return code is "IEF" or "HEF";
        }

        private static string GetJobId(string? currentFilePath, InspectionFile? inspection)
        {
            string source = inspection?.InspectionNumber ?? Path.GetFileNameWithoutExtension(currentFilePath ?? "");
            return source.Split('-').FirstOrDefault() ?? "";
        }

        private static IEnumerable<string> FindSameJobEnergyFinalFiles(string? currentFilePath, string jobId)
        {
            if (string.IsNullOrWhiteSpace(currentFilePath) || string.IsNullOrWhiteSpace(jobId))
                return Array.Empty<string>();

            string? currentDir = Path.GetDirectoryName(currentFilePath);
            if (string.IsNullOrWhiteSpace(currentDir)) return Array.Empty<string>();

            var directories = new List<string> { currentDir };
            string? parent = Path.GetDirectoryName(currentDir);
            if (!string.IsNullOrWhiteSpace(parent))
            {
                foreach (string sibling in new[] { "MyList", "Review", "Archive" })
                {
                    string path = Path.Combine(parent, sibling);
                    if (Directory.Exists(path) && !directories.Contains(path, StringComparer.OrdinalIgnoreCase))
                        directories.Add(path);
                }
            }

            var files = new List<string>();
            foreach (string dir in directories)
            {
                try
                {
                    files.AddRange(Directory.GetFiles(dir, $"{jobId}-*-*.ins", SearchOption.TopDirectoryOnly)
                        .Where(path => Regex.IsMatch(Path.GetFileName(path),
                            $@"^{Regex.Escape(jobId)}-(?:IEF|HEF)-\d+-", RegexOptions.IgnoreCase)));
                }
                catch
                {
                    // Dropbox/FileProvider can transiently block a folder. Continue with others.
                }
            }

            return files
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(GetAttempt)
                .ThenByDescending(path =>
                {
                    try { return File.GetLastWriteTimeUtc(path); }
                    catch { return DateTime.MinValue; }
                })
                .ToList();
        }

        private static int GetAttempt(string path)
        {
            var match = Regex.Match(Path.GetFileNameWithoutExtension(path), @"-(?:IEF|HEF)-(\d+)-", RegexOptions.IgnoreCase);
            return match.Success && int.TryParse(match.Groups[1].Value, out int attempt) ? attempt : 0;
        }

        private static IEnumerable<(int UnitNumber, string OutdoorModel, string IndoorModel)> ExtractEquipmentPairs(
            InspectionFile inspection)
        {
            var outdoor = ExtractSectionModels(inspection, section =>
                (section.Name ?? "").Contains("Condenser", StringComparison.OrdinalIgnoreCase));
            var indoor = ExtractSectionModels(inspection, section =>
                (section.Name ?? "").Contains("Air Handler", StringComparison.OrdinalIgnoreCase) ||
                (section.Name ?? "").Contains("Evaporator", StringComparison.OrdinalIgnoreCase));

            foreach (int unit in outdoor.Keys.Union(indoor.Keys).OrderBy(x => x))
            {
                if (outdoor.TryGetValue(unit, out string? outdoorModel) &&
                    indoor.TryGetValue(unit, out string? indoorModel))
                    yield return (unit, outdoorModel, indoorModel);
            }
        }

        private static Dictionary<int, string> ExtractSectionModels(
            InspectionFile inspection,
            Func<Section, bool> sectionMatch)
        {
            var result = new Dictionary<int, string>();
            foreach (var item in inspection.Sections.Where(sectionMatch).SelectMany(section => section.Items))
            {
                string name = item.Name ?? "";
                if (!name.Contains("Model", StringComparison.OrdinalIgnoreCase)) continue;

                string value = item.Value?.ToString()?.Trim() ?? "";
                if (NormalizeModel(value).Length == 0) continue;

                int unit = Regex.IsMatch(name, @"unit\s*2", RegexOptions.IgnoreCase) ? 2 : 1;
                result.TryAdd(unit, value);
            }
            return result;
        }
    }
}
