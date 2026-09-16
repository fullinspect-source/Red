using InspectionEditor.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace InspectionEditor.Services
{
    // A UI-thread snapshot of explicit targets, never inspection results or inferred code defaults.
    public static class TestingTargetsService
    {
        private static readonly (string Number, string Name, string Key)[] Fields = {
            ("1.1", "Total Duct Leakage Max. CFM", "DUCTLEAKAGEMAXCFM"),
            ("1.2", "Total Duct Leakage Max. CFM (unit 2)", "DUCTLEAKAGEMAXCFM2"),
            ("1.3", "Blower Door Max. CFM", "BLOWERDOORMAXCFM"),
            ("1.4", "AC Square Footage", "CONDITIONEDFLOORAREA"),
            ("1.5", "AC Volume", "CONDITIONEDVOLUME"),
            ("1.6", "(HVAC) Target fresh air CFM", "TARGETFRESHAIRCFM"),
            ("1.7", "(HVAC) Target run time", "TARGETRUNTIME")
        };

        public static void Refresh(EnergyComplianceInfo info, InspectionFile? inspection)
        {
            info.TestingTargets.Clear();
            info.TestingTargetSources.Clear();
            if (inspection == null || !string.Equals(inspection.InspectionCode?.Trim(), "HET", StringComparison.OrdinalIgnoreCase)) return;
            var sections = inspection.Sections?.Where(s => s.Number == "1" &&
                string.Equals(s.Name?.Trim(), "Testing Targets", StringComparison.OrdinalIgnoreCase)).ToList();
            if (sections?.Count != 1) return;
            foreach (var field in Fields)
            {
                var items = sections[0].Items?.Where(i => i.Number == field.Number &&
                    string.Equals(i.Name?.Trim(), field.Name, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(i.ControlName, "Text", StringComparison.OrdinalIgnoreCase)).ToList();
                if (items?.Count != 1) continue;
                string value = items[0].Value?.ToString()?.Trim() ?? "";
                if (!Regex.IsMatch(value, @"^(?:\d+|\d{1,3}(?:,\d{3})+)(?:\.\d+)?$") ||
                    !decimal.TryParse(value, NumberStyles.AllowThousands | NumberStyles.AllowDecimalPoint,
                        CultureInfo.InvariantCulture, out var number) || number <= 0) continue;
                info.TestingTargets[field.Key] = value;
                info.TestingTargetSources[field.Key] = $"Section 1.{field.Number.Split('.')[1]} Testing Targets (current inspection)";
            }
        }

        public static string CanonicalKey(string? key) => ExtractionMappingService.NormalizeFieldKey(key ?? "") switch {
            "EFFECTIVEBLOWERDOORCFM" or "BLOWERDOORCFM50PA" => "BLOWERDOORMAXCFM",
            "EFFECTIVEDUCTLEAKAGECFM" or "DUCTLEAKAGECFM25PA" => "DUCTLEAKAGEMAXCFM",
            "FRESHAIRCFM" => "TARGETFRESHAIRCFM",
            "RUNTIME" => "TARGETRUNTIME",
            var normalized => normalized
        };

        public static string? ItemKey(string? code, string? number)
        {
            if (EnergyComplianceService.NormalizeCode(code) != "HET") return null;
            return number switch {
                "2.12" => "BLOWERDOORMAXCFM",
                "3.3" => "DUCTLEAKAGEMAXCFM",
                "3.4" => "DUCTLEAKAGEMAXCFM2",
                "5.5" => "TARGETFRESHAIRCFM",
                "5.7" => "TARGETRUNTIME",
                _ => null
            };
        }

        public static string? Value(EnergyComplianceInfo info, string? key)
            => key != null && info.TestingTargets.TryGetValue(CanonicalKey(key), out var value) ? value : null;
        public static string? Source(EnergyComplianceInfo info, string? key)
            => key != null && info.TestingTargetSources.TryGetValue(CanonicalKey(key), out var source) ? source : null;
    }
}
