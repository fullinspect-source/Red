#nullable enable
using InspectionEditor.Models; using System; using System.Linq; using System.Collections.Generic; using System.Text.RegularExpressions;
namespace InspectionEditor.Services {
    public class EnergyComplianceInfo
    {
        public string? PdfPath    { get; set; }
        public string? DisplayName { get; set; }
        public string? StatusText  { get; set; }

        // Kept separate from EC fields: never fed into code-default calculations.
        public Dictionary<string, string> TestingTargets { get; } = new();
        public Dictionary<string, string> TestingTargetSources { get; } = new();
        public bool HasAvailableTargets => ExtractedFieldCount > 0 || !string.IsNullOrWhiteSpace(EnergyStarProgram) || !string.IsNullOrWhiteSpace(IECCVersion) || TestingTargets.Count > 0;

        // Summary
        public string? HersIndex { get; set; }

        // Building
        public string? ConditionedFloorArea { get; set; }  // "2,199"
        public string? ConditionedVolume    { get; set; }  // "28,237"
        public string? NumberOfBedrooms     { get; set; }

        // Air sealing
        public string? BlowerDoorMaxCfm { get; set; }     // CFM @ 50 Pa
        public string? BlowerDoorSourceAch { get; set; }  // ACH source when CFM is calculated from volume

        // Ducts
        public string? DuctLeakageMaxCfm { get; set; }    // CFM @ 25 Pa (total)
        public string? NumberOfReturns   { get; set; }
        public string? SupplyDuctR       { get; set; }    // "R6"
        public string? ReturnDuctR       { get; set; }    // "R6"

        // Fenestration
        public string? WindowUFactor { get; set; }
        public string? WindowSHGC    { get; set; }

        // Insulation
        public string? SlopedCeilingR { get; set; }       // IER 3.2
        public string? AtticCeilingR  { get; set; }       // IEF 8.2 (vented-attic floor)
        public string? WallR          { get; set; }       // majority value: "R13"
        public string? WallRDetails   { get; set; }       // all values: "R13 ×5, R19 ×1"
        public string? AtticWallR     { get; set; }       // foam encapsulated → IER 11.1
        public string? AtticRoofR     { get; set; }       // foam encapsulated → IER 11.3
        public bool?   RadiantBarrier { get; set; }

        // Hot water
        public string? HotWaterPipeR      { get; set; }  // "R3"
        public string? WaterHeaterFuel    { get; set; }  // "Gas" / "Electric"
        public string? WaterHeaterCapacity { get; set; } // "Tankless" / "50 Gallon"

        // HVAC
        public string? HvacCoolingSeer  { get; set; }    // "15.2 SEER2"
        public string? HvacCoolingCapacityKbtu { get; set; } // Explicit capacity, not nominal tonnage
        public string? HvacTonnage      { get; set; }    // "3"
        public string? DesignAirflowCfm { get; set; }   // unit 1; EC tonnage × 360 unless an equipment matchup overrides it
        public string? DesignAirflowCfm2 { get; set; }
        public string? DesignAirflowSource { get; set; }
        public string? DesignAirflowSourceFile { get; set; }
        public string? DesignAirflowOutdoorModel { get; set; }
        public string? DesignAirflowIndoorModel { get; set; }
        public string? DesignAirflowOutdoorModel2 { get; set; }
        public string? DesignAirflowIndoorModel2 { get; set; }
        internal string? DesignAirflowFallbackSource { get; set; }
        internal string? DesignAirflowFallbackCfm { get; set; }
        internal string? DesignAirflowFallbackCfm2 { get; set; }
        internal string? DesignAirflowFallbackStatusText { get; set; }
        internal string? DesignAirflowFallbackDisplayName { get; set; }

        // Ventilation
        public string? TargetFreshAirCfm { get; set; }
        public string? TargetRunTime     { get; set; }   // "9.9 hrs/day (41%)"
        public string? VentFanWatts      { get; set; }

        // Program
        public string? EnergyStarProgram { get; set; }
        public string? IECCVersion       { get; set; }  // "IECC 2015" / "IECC 2021"

        // Count available parsed/calculated fields, excluding code-default targets and metadata. This is not
        // an applicability/completeness score and does not broaden application eligibility.
        public int ExtractedFieldCount => new string?[] {
            HersIndex, ConditionedFloorArea, ConditionedVolume, NumberOfBedrooms,
            BlowerDoorMaxCfm, DuctLeakageMaxCfm, NumberOfReturns, SupplyDuctR, ReturnDuctR,
            WindowUFactor, WindowSHGC, SlopedCeilingR, AtticCeilingR, WallR, AtticWallR,
            AtticRoofR, HotWaterPipeR, WaterHeaterFuel, WaterHeaterCapacity,
            HvacCoolingSeer, HvacCoolingCapacityKbtu, HvacTonnage, DesignAirflowCfm, DesignAirflowCfm2,
            TargetFreshAirCfm, TargetRunTime, VentFanWatts
        }.Count(v => !string.IsNullOrWhiteSpace(v));
        public string ExtractionStatus => ExtractedFieldCount > 0
            ? $"EC report: {ExtractedFieldCount} fields available (parsed/calculated). Review available values; unrecognized or inapplicable fields remain blank."
            : "EC report found but no supported data fields could be extracted.";

        public bool IsLoaded =>
            HersIndex != null || ConditionedFloorArea != null || BlowerDoorMaxCfm != null ||
            DesignAirflowCfm != null || DesignAirflowCfm2 != null;

        // ── Derived targets (used when OCR can't find the value directly) ──

        // Whole-house air infiltration @ 50 Pa derived from volume.
        // Energy Star 3.1 / IECC code minimum: 5.0 ACH50 → CFM50 = Volume × 5.0 / 60
        public string? BlowerDoorDerivedCfm
        {
            get
            {
                if (BlowerDoorMaxCfm != null) return null; // not needed
                if (!double.TryParse(ConditionedVolume?.Replace(",", ""), out double vol) || vol <= 0) return null;
                return ((int)Math.Round(vol * 5.0 / 60.0)).ToString();
            }
        }

        // Energy Star v3.2 target: 4.5 ACH50 → CFM50 = Volume × 4.5 / 60
        public string? BlowerDoorDerivedCfmEs32
        {
            get
            {
                if (!double.TryParse(ConditionedVolume?.Replace(",", ""), out double vol) || vol <= 0) return null;
                return ((int)Math.Round(vol * 4.5 / 60.0)).ToString();
            }
        }

        // Duct leakage @ 25 Pa derived from conditioned floor area.
        // Texas energy code: max 4 CFM25 per 100 sq ft → CFM25 = Area × 0.04
        public string? DuctLeakageDerivedCfm
        {
            get
            {
                if (DuctLeakageMaxCfm != null) return null; // not needed
                if (!double.TryParse(ConditionedFloorArea?.Replace(",", ""), out double area) || area <= 0) return null;
                return ((int)Math.Round(area * 0.04)).ToString();
            }
        }

        // Effective values — found value if available, otherwise derived
        public string? EffectiveBlowerDoorCfm => BlowerDoorMaxCfm ?? BlowerDoorDerivedCfm;
        public string? EffectiveDuctLeakageCfm => DuctLeakageMaxCfm ?? DuctLeakageDerivedCfm;
    }

public static class EnergyComplianceService {
        internal static string NormalizeCode(string? code) => (code ?? "").ToUpperInvariant() switch
        {
            "HER"  => "IER",
            "QIER" => "IER",
            "HEF"  => "IEF",
            var c  => c
        };
        public static string? GetValueForField(EnergyComplianceInfo info, string? fieldKey)
        {
            if (info == null || string.IsNullOrWhiteSpace(fieldKey)) return null;
            return ExtractionMappingService.NormalizeFieldKey(fieldKey) switch
            {
                "HERSINDEX" => info.HersIndex,
                "CONDITIONEDFLOORAREA" => info.ConditionedFloorArea,
                "CONDITIONEDVOLUME" => info.ConditionedVolume,
                "NUMBEROFBEDROOMS" => info.NumberOfBedrooms,
                "BLOWERDOORMAXCFM" or "BLOWERDOORCFM50PA" => info.BlowerDoorMaxCfm,
                "EFFECTIVEBLOWERDOORCFM" => info.EffectiveBlowerDoorCfm,
                "DUCTLEAKAGEMAXCFM" or "DUCTLEAKAGECFM25PA" => info.DuctLeakageMaxCfm,
                "EFFECTIVEDUCTLEAKAGECFM" => info.EffectiveDuctLeakageCfm,
                "NUMBEROFRETURNS" => info.NumberOfReturns,
                "SUPPLYDUCTR" => info.SupplyDuctR,
                "RETURNDUCTR" => info.ReturnDuctR,
                "WINDOWUFACTOR" => info.WindowUFactor,
                "WINDOWSHGC" => info.WindowSHGC,
                "SLOPEDCEILINGR" => info.SlopedCeilingR,
                "ATTICCEILINGR" => info.AtticCeilingR,
                "WALLR" => info.WallR,
                "WALLRDETAILS" => info.WallRDetails,
                "ATTICWALLR" => info.AtticWallR,
                "ATTICROOFR" => info.AtticRoofR,
                "HOTWATERPIPER" => info.HotWaterPipeR,
                "WATERHEATERFUEL" => info.WaterHeaterFuel,
                "WATERHEATERCAPACITY" => info.WaterHeaterCapacity,
                "HVACCOOLINGSEER" or "COOLINGSEER" => info.HvacCoolingSeer,
                "HVACTONNAGE" or "TONNAGE" => info.HvacTonnage,
                "DESIGNAIRFLOWCFM" => info.DesignAirflowCfm,
                "DESIGNAIRFLOWCFM2" => info.DesignAirflowCfm2,
                "TARGETFRESHAIRCFM" or "FRESHAIRCFM" => info.TargetFreshAirCfm,
                "TARGETRUNTIME" or "RUNTIME" => info.TargetRunTime,
                "VENTFANWATTS" or "FANWATTS" => info.VentFanWatts,
                "ENERGYSTARPROGRAM" => info.EnergyStarProgram,
                "ENERGYSTARPROGRAMORIECCVERSION" => info.EnergyStarProgram ?? info.IECCVersion,
                "ENERGYSTARPROGRAMORIECCVERSIONYEAR" => info.EnergyStarProgram != null
                    ? "Energy Star"
                    : info.IECCVersion?.Replace("IECC ", "").Replace("IECC", "").Trim(),
                "IECCVERSION" => info.IECCVersion,
                "IECCVERSIONYEAR" => info.IECCVersion?.Replace("IECC ", "").Replace("IECC", "").Trim(),
                "PERFORMANCEPATH" => "Performance IECC",
                _ => null
            };
        }
        public static string? GetLabelForField(string? fieldKey)
        {
            if (string.IsNullOrWhiteSpace(fieldKey)) return null;
            return ExtractionMappingService.NormalizeFieldKey(fieldKey) switch
            {
                "HERSINDEX" => "HERS",
                "CONDITIONEDFLOORAREA" => "Floor Area",
                "CONDITIONEDVOLUME" => "Volume",
                "NUMBEROFBEDROOMS" => "Bedrooms",
                "BLOWERDOORMAXCFM" or "BLOWERDOORCFM50PA" or "EFFECTIVEBLOWERDOORCFM" => "Blower Door",
                "DUCTLEAKAGEMAXCFM" or "DUCTLEAKAGECFM25PA" or "EFFECTIVEDUCTLEAKAGECFM" => "Duct Leakage",
                "NUMBEROFRETURNS" => "Returns",
                "SUPPLYDUCTR" => "Supply Duct",
                "RETURNDUCTR" => "Return Duct",
                "WINDOWUFACTOR" => "U-factor",
                "WINDOWSHGC" => "SHGC",
                "SLOPEDCEILINGR" => "Sloped Ceiling",
                "ATTICCEILINGR" => "Attic Ceiling",
                "WALLR" or "WALLRDETAILS" => "Wall",
                "ATTICWALLR" => "Attic Wall",
                "ATTICROOFR" => "Attic Roof",
                "HOTWATERPIPER" => "Pipe",
                "WATERHEATERFUEL" => "WH Fuel",
                "WATERHEATERCAPACITY" => "WH Capacity",
                "HVACCOOLINGSEER" or "COOLINGSEER" => "SEER",
                "HVACTONNAGE" or "TONNAGE" => "Tonnage",
                "DESIGNAIRFLOWCFM" => "Design Airflow",
                "DESIGNAIRFLOWCFM2" => "Design Airflow (unit 2)",
                "TARGETFRESHAIRCFM" or "FRESHAIRCFM" => "Fresh Air",
                "TARGETRUNTIME" or "RUNTIME" => "Run Time",
                "VENTFANWATTS" or "FANWATTS" => "Fan Watts",
                "ENERGYSTARPROGRAM" or "ENERGYSTARPROGRAMORIECCVERSION" => "Energy Star",
                "ENERGYSTARPROGRAMORIECCVERSIONYEAR" => "IECC Code",
                "IECCVERSION" or "IECCVERSIONYEAR" => "IECC Code",
                "PERFORMANCEPATH" => "Type",
                _ => null
            };
        }
        public static bool ApplySingleItem(EnergyComplianceInfo info, Item item, string? inspCode)
        {
            return ApplySingleItem(info, item, inspCode, null);
        }
        public static bool ApplySingleItem(EnergyComplianceInfo info, Item item, string? inspCode, Section? section)
        {
            var resolved = EnergySemanticMappingService.Resolve(info, inspCode, section, item);
            return resolved is { CanApply: true } && SetItemValue(item, resolved.Value);
        }
        public static int ApplyToInspection(EnergyComplianceInfo info, InspectionFile inspection)
        {
            if (info == null || inspection == null) return 0;
            int count = 0;
            foreach (var section in inspection.Sections)
            foreach (var item in section.Items)
            {
                if (!string.IsNullOrWhiteSpace(item.Value?.ToString())) continue;
                if (ApplySingleItem(info, item, inspection.InspectionCode, section)) count++;
            }
            return count;
        }
        private static bool SetItemValue(Item item, string value)
        {
            string ctrl = (item.ControlName ?? "").ToLowerInvariant();
            if (ctrl is "passfail" or "passfailnani" or "yesno" or "yesnonani") return false;

            if (ctrl is "text" or "textnani" or "memo" or "numberpad" or "numberpadnani")
            {
                item.Value = value;
                return true;
            }

            if (ctrl is "lookup" or "lookupnani")
            {
                string? best = BestLookupMatch(item, value);
                if (best == null) return false;
                item.Value = best;
                return true;
            }

            if (ctrl is "yesno" or "yesnonani")
            {
                item.Value = value;
                return true;
            }

            // Unknown controls cannot be assumed to be value-entry controls.
            return false;
        }
        private static string? BestLookupMatch(Item item, string target)
        {
            // Only full equivalent options are safe. Substrings turn R3 into R30,
            // 15 SEER into 15 SEER2, or a fuel into an unrelated equipment model.
            string Canonical(string v) => Regex.Replace(v.Trim().ToUpperInvariant(), @"\s+", " ");
            var exact = item.ValueList?.Where(v => v != null && Canonical(v) == Canonical(target)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (exact?.Count == 1) return exact[0];
            string R(string v) => Regex.Replace(v.Trim().ToUpperInvariant(), @"^R[- ]+(?=\d)", "R");
            if (Regex.IsMatch(R(target), @"^R\d+(?:\.\d+)?$"))
            {
                var equivalents = item.ValueList?.Where(v => v != null && R(v) == R(target)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (equivalents?.Count == 1) return equivalents[0];
            }
            return null;
        }
} public static class ExtractionMappingService { public static string NormalizeFieldKey(string? s) => Regex.Replace((s??"").ToUpperInvariant(), "[^A-Z0-9]", ""); } }