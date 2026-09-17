using InspectionEditor.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace InspectionEditor.Services
{
    /// <summary>Prompt semantics, never a numeric form position, authorize energy suggestions.</summary>
    public static class EnergySemanticMappingService
    {
        public sealed record ResolvedValue(string FieldKey, string Value, string Label, string Units, string Source, bool CanApply);
        private static string N(string? value) => Regex.Replace((value ?? "").ToLowerInvariant(), @"[^a-z0-9]+", " ").Trim();
        private static bool Is(string value, params string[] aliases) => aliases.Any(a => value == N(a));

        public static ResolvedValue? Resolve(EnergyComplianceInfo info, string? inspectionCode, Section? section, Item item, string? requestedFieldKey = null)
        {
            if (info == null || item == null) return null;
            string code = EnergyComplianceService.NormalizeCode(inspectionCode?.Trim());
            if (code is not ("IER" or "IEF" or "HET" or "IET" or "AFI" or "PLY" or "PS" or "ACI")) return null;
            string name = N(item.Name), context = N(section?.Name), control = N(item.ControlName);
            if (control is not ("text" or "textnani" or "memo" or "numberpad" or "numberpadnani" or "lookup" or "lookupnani")) return null;
            var unitIds = Regex.Matches(name + " " + context, @"\b(?:unit|system)\s*(\d+)\b").Cast<Match>()
                .Select(m => m.Groups[1].Value).Distinct().ToList();
            if (unitIds.Count > 1 || unitIds.Any(u => u is not ("1" or "2"))) return null;
            bool unit2 = unitIds.Contains("2") || name.EndsWith(" 2");
            string baseName = Regex.Replace(name, @"\s+unit\s*2$", "");
            string? key = null; bool apply = true; string units = "";
            bool equipment = Is(context, "Condenser Unit", "Evaporator Coil/Air Handler Unit", "Air Handler Unit", "Evaporator Coil");
            // IDs may show airflow guidance only when the equipment context is unambiguous.
            if (Regex.IsMatch(name, @"\b(model|serial)\b"))
            {
                if (code != "IEF" || !equipment || !Is(baseName, "Unit: Make/Model", "Unit: Serial Number")) return null;
                key = unit2 ? "DESIGNAIRFLOWCFM2" : "DESIGNAIRFLOWCFM"; units = "CFM"; apply = false;
            }
            else if (Is(name, "AC Square Footage", "Enter conditioned floor area", "Conditioned floor area", "Total Square Footage")) { key = "CONDITIONEDFLOORAREA"; units = "ft²"; }
            else if (Is(name, "AC Volume", "Conditioned volume")) { key = "CONDITIONEDVOLUME"; units = "ft³"; }
            else if (Is(name, "Number of bedrooms", "Bedrooms")) key = "NUMBEROFBEDROOMS";
            else if (Is(name, "Number of returns", "Enter number of returns")) key = "NUMBEROFRETURNS";
            else if (Is(name, "IECC code")) key = "ENERGYSTARPROGRAMORIECCVERSIONYEAR";
            else if (Is(name, "Energy Star program", "Energy Star version")) key = "ENERGYSTARPROGRAM";
            else if (Is(name, "Type") && Is(context, "High Performance Fenestration")) key = "PERFORMANCEPATH";
            else if (Is(name, "Performance/Prescriptive path", "Performance or Prescriptive", "Compliance path", "Energy compliance path")) key = "PERFORMANCEPATH";
            else if (Is(name, "Window U-factor", "U-factor", "Window U factor", "Area weighted average U-factor")) key = "WINDOWUFACTOR";
            else if (Is(name, "SHGC", "Window SHGC", "Area weighted average SHGC")) key = "WINDOWSHGC";
            else if (Is(name, "Sloped ceiling R-value", "Sloped Ceiling Insulation R-value")) { key = "SLOPEDCEILINGR"; units = "R-value"; }
            else if (Is(name, "Wall R-value", "Wall Insulation R-value", "Wood frame wall R-value (MRF)")) { key = "WALLR"; units = "R-value"; }
            else if (Is(name, "Ceiling R-value", "Attic ceiling R-value") && Is(context, "Quality Installed Insulation", "Insulation", "Attic Insulation")) { key = "ATTICCEILINGR"; units = "R-value"; }
            else if (Is(name, "Supply duct R-value", "Supply Duct Insulation R-value")) { key = "SUPPLYDUCTR"; units = "R-value"; }
            else if (Is(name, "Return duct R-value", "Return Duct Insulation R-value")) { key = "RETURNDUCTR"; units = "R-value"; }
            else if (Is(name, "Hot water pipe R-value", "Hot Water Pipe Insulation R-value", "Hot water piping insulation R-value")) { key = "HOTWATERPIPER"; units = "R-value"; }
            else if (Is(name, "Attic wall R-value", "Required attic wall R-value")) { key = "ATTICWALLR"; units = "R-value"; }
            else if (Is(name, "Attic roof R-value", "Required attic roof R-value")) { key = "ATTICROOFR"; units = "R-value"; }
            else if (Is(context, "Plumbing Hot Water Service", "Water Heater", "Water Heating") && Is(name, "Gas/Electric", "Fuel type", "Water heater fuel")) key = "WATERHEATERFUEL";
            else if (Is(context, "Plumbing Hot Water Service", "Water Heater", "Water Heating") && Is(name, "Capacity", "Water heater capacity")) key = "WATERHEATERCAPACITY";
            else if (Is(name, "HVAC SEER", "Cooling SEER", "HVAC cooling SEER")) key = "HVACCOOLINGSEER";
            else if (Is(name, "Design airflow", "Design airflow - (tonnage x 360CFM)(1)", "Design airflow - (tonnage x 360CFM)(2)")) { key = unit2 ? "DESIGNAIRFLOWCFM2" : "DESIGNAIRFLOWCFM"; units = "CFM"; }
            else if (Is(baseName, "Total Duct Leakage Max. CFM")) { key = unit2 ? "DUCTLEAKAGEMAXCFM2" : "DUCTLEAKAGEMAXCFM"; units = "CFM25"; }
            else if (Is(name, "Blower Door Max. CFM")) { key = "BLOWERDOORMAXCFM"; units = "CFM50"; }
            else if (Is(name, "(HVAC) Target fresh air CFM")) { key = "TARGETFRESHAIRCFM"; units = "CFM"; }
            else if (Is(name, "(HVAC) Target run time")) key = "TARGETRUNTIME";
            // Actual values are NEVER writable from targets. No total-leakage target on leakage-to-outside rows.
            else if (Is(name, "Blower door test #1 (50 Pa) CFM")) { key = "BLOWERDOORMAXCFM"; units = "CFM50"; apply = false; }
            else if (Is(name, "Duct system #1 total leakage CFM", "Duct system #2 total leakage CFM")) { key = unit2 ? "DUCTLEAKAGEMAXCFM2" : "DUCTLEAKAGEMAXCFM"; units = "CFM25"; apply = false; }
            else if (Is(name, "(HVAC) Measured fresh air CFM")) { key = "TARGETFRESHAIRCFM"; units = "CFM"; apply = false; }
            else if (Is(name, "(HVAC) Set run time (hours of run time or min. or %)")) { key = "TARGETRUNTIME"; apply = false; }
            else if (Is(name, "(HVAC) Fan watts")) { key = "VENTFANWATTS"; units = "W"; apply = false; }
            if (key == null) return null;
            if (requestedFieldKey != null && TestingTargetsService.CanonicalKey(requestedFieldKey) != TestingTargetsService.CanonicalKey(key)) return null;
            string? value = key == "DESIGNAIRFLOWCFM2" ? info.DesignAirflowCfm2 : EnergyComplianceService.GetValueForField(info, key);
            string source = "EC report";
            if (key.StartsWith("DESIGNAIRFLOW"))
            {
                source = EquipmentAirflowService.GetSourceForUnit(info, unit2 ? 2 : 1) ?? "EC tonnage × 360 (calculated)";
                if (source.StartsWith("STRADA", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(info.DesignAirflowSourceFile)) source += ": " + info.DesignAirflowSourceFile;
            }
            else if (string.IsNullOrWhiteSpace(value))
            {
                value = TestingTargetsService.Value(info, key);
                source = TestingTargetsService.Source(info, key) ?? "Testing Targets";
                if (!string.IsNullOrWhiteSpace(value)) apply = false;
            }
            // Loading an EC report does not prove its compliance path.
            if (key == "PERFORMANCEPATH") return null;
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (source.StartsWith("Section 1", StringComparison.OrdinalIgnoreCase) && Is(context, "Testing Targets")) return null;
            string label = key == "DESIGNAIRFLOWCFM2" ? "Design Airflow (unit 2)" : EnergyComplianceService.GetLabelForField(key) ?? key;
            return new ResolvedValue(key, value, label, units, source, apply);
        }
    }
}
