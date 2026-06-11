using InspectionEditor.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Docnet.Core;
using Docnet.Core.Models;
using Tesseract;
using UglyToad.PdfPig;
using DrawingImageFormat = System.Drawing.Imaging.ImageFormat;

namespace InspectionEditor.Services
{
    public class EnergyComplianceInfo
    {
        public string? PdfPath    { get; set; }
        public string? DisplayName { get; set; }
        public string? StatusText  { get; set; }

        // Summary
        public string? HersIndex { get; set; }

        // Building
        public string? ConditionedFloorArea { get; set; }  // "2,199"
        public string? ConditionedVolume    { get; set; }  // "28,237"
        public string? NumberOfBedrooms     { get; set; }

        // Air sealing
        public string? BlowerDoorMaxCfm { get; set; }     // CFM @ 50 Pa

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
        public string? HvacTonnage      { get; set; }    // "3"
        public string? DesignAirflowCfm { get; set; }   // tonnage × 360

        // Ventilation
        public string? TargetFreshAirCfm { get; set; }
        public string? TargetRunTime     { get; set; }   // "9.9 hrs/day (41%)"
        public string? VentFanWatts      { get; set; }

        // Program
        public string? EnergyStarProgram { get; set; }
        public string? IECCVersion       { get; set; }  // "IECC 2015" / "IECC 2021"

        public bool IsLoaded =>
            HersIndex != null || ConditionedFloorArea != null || BlowerDoorMaxCfm != null;

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

    public static class EnergyComplianceService
    {
        // ---------------------------------------------------------------
        // Mapping: (normalized code, item number) → field getter
        // HER aliases to IER, HEF aliases to IEF
        // ---------------------------------------------------------------
        private static readonly Dictionary<(string code, string num), Func<EnergyComplianceInfo, string?>> Mappings
            = new()
        {
            // IER / HER
            { ("IER", "1.3"),   i => i.EnergyStarProgram ?? i.IECCVersion },
            // 1.4 IECC code Lookup: "Energy Star" if ES program, else year only (e.g. "2021" from "IECC 2021")
            { ("IER", "1.4"),   i => i.EnergyStarProgram != null ? "Energy Star"
                                     : i.IECCVersion?.Replace("IECC ", "").Replace("IECC", "").Trim() },
            { ("IER", "2.2"),   i => i.WindowUFactor },
            { ("IER", "2.3"),   i => i.WindowSHGC },
            { ("IER", "3.2"),   i => i.SlopedCeilingR },
            { ("IER", "3.3"),   i => i.WallR },
            { ("IER", "8.1"),   i => i.SupplyDuctR },
            { ("IER", "8.2"),   i => i.ReturnDuctR },
            { ("IER", "9.1"),   i => i.HotWaterPipeR },
            { ("IER", "10.2"),  i => i.ConditionedFloorArea },
            { ("IER", "11.1"),  i => i.DuctLeakageMaxCfm },   // Duct Blaster: total duct leakage max CFM
            { ("IER", "12.1"),  i => i.AtticWallR },           // Foam section: required attic wall R-value

            // HET
            { ("HET", "1.1"),   i => i.DuctLeakageMaxCfm },
            { ("HET", "1.3"),   i => i.BlowerDoorMaxCfm },
            { ("HET", "1.4"),   i => i.ConditionedFloorArea },
            { ("HET", "1.5"),   i => i.ConditionedVolume },
            { ("HET", "1.6"),   i => i.TargetFreshAirCfm },
            { ("HET", "1.7"),   i => i.TargetRunTime },
            { ("HET", "2.12"),  i => i.EffectiveBlowerDoorCfm },
            { ("HET", "3.1"),   i => i.NumberOfReturns },
            { ("HET", "3.3"),   i => i.EffectiveDuctLeakageCfm },
            { ("HET", "3.4"),   i => i.EffectiveDuctLeakageCfm },
            { ("HET", "3.5"),   i => i.EffectiveDuctLeakageCfm },
            { ("HET", "3.6"),   i => i.EffectiveDuctLeakageCfm },
            { ("HET", "5.6"),   i => i.VentFanWatts },

            // IEF / HEF
            { ("IEF", "3.4"),   i => i.WaterHeaterFuel },
            { ("IEF", "3.5"),   i => i.WaterHeaterCapacity },
            { ("IEF", "4.2"),   i => i.HvacCoolingSeer },
            { ("IEF", "8.2"),   i => i.AtticCeilingR },
            { ("IEF", "10.8"),  i => i.ConditionedFloorArea },
            { ("IEF", "10.9"),  i => i.ConditionedVolume },
            // Bedrooms: older IEF forms use 10.11; newer forms use 10.13 — map both, non-existent item silently skipped
            { ("IEF", "10.11"), i => i.NumberOfBedrooms },
            { ("IEF", "10.13"), i => i.NumberOfBedrooms },
            // Newer IEF form variant (2560090-style): blower door max and duct leakage in section 11
            { ("IEF", "11.1"),  i => i.EffectiveBlowerDoorCfm },
            { ("IEF", "11.16"), i => i.EffectiveDuctLeakageCfm },
            { ("IEF", "12.5"),  i => i.TargetFreshAirCfm },

            // AFI
            { ("AFI", "1.4"),   i => i.NumberOfReturns },
            { ("AFI", "1.5"),   i => i.ConditionedFloorArea },
            { ("AFI", "2.1"),   i => i.DesignAirflowCfm },
            { ("AFI", "2.5"),   i => i.DesignAirflowCfm },
            { ("AFI", "3.1"),   i => i.DesignAirflowCfm },
            { ("AFI", "3.9"),   i => i.DesignAirflowCfm },

            // PLY (Polyseal / Energy Star air-barrier inspection) — 2.1 is Performance/Prescriptive path
            { ("PLY", "2.1"),   _ => "Performance IECC" },

            // IER 2.1 — Performance path (if EC report exists, it's always Performance)
            { ("IER", "2.1"),   _ => "Performance IECC" },

            // ACI — if/when introduced; same 2.1 path selection
            { ("ACI", "2.1"),   _ => "Performance IECC" },

            // PS — Polyseal variant code; same 2.1 path selection
            { ("PS",  "2.1"),   _ => "Performance IECC" },
        };

        private static readonly Dictionary<(string code, string num), string> Labels = new()
        {
            { ("IER", "1.3"),   "Energy Star" },
            { ("IER", "1.4"),   "IECC Code" },
            { ("IER", "2.2"),   "U-factor" },
            { ("IER", "2.3"),   "SHGC" },
            { ("IER", "3.2"),   "Sloped Ceiling" },
            { ("IER", "3.3"),   "Wall" },
            { ("IER", "8.1"),   "Supply Duct" },
            { ("IER", "8.2"),   "Return Duct" },
            { ("IER", "9.1"),   "Pipe" },
            { ("IER", "10.2"),  "Floor Area" },
            { ("IER", "11.1"),  "Duct Leakage" },
            { ("IER", "12.1"),  "Attic Wall" },
            { ("HET", "1.1"),   "Duct Leakage" },
            { ("HET", "1.3"),   "Blower Door" },
            { ("HET", "1.4"),   "Floor Area" },
            { ("HET", "1.5"),   "Volume" },
            { ("HET", "1.6"),   "Fresh Air" },
            { ("HET", "1.7"),   "Run Time" },
            { ("HET", "2.12"),  "Blower Door" },
            { ("HET", "3.1"),   "Returns" },
            { ("HET", "3.3"),   "Duct Leakage" },
            { ("HET", "3.4"),   "Duct Leakage" },
            { ("HET", "3.5"),   "Duct Leakage" },
            { ("HET", "3.6"),   "Duct Leakage" },
            { ("HET", "5.6"),   "Fan Watts" },
            { ("IEF", "3.4"),   "WH Fuel" },
            { ("IEF", "3.5"),   "WH Capacity" },
            { ("IEF", "4.2"),   "SEER" },
            { ("IEF", "8.2"),   "Attic Ceiling" },
            { ("IEF", "10.8"),  "Floor Area" },
            { ("IEF", "10.9"),  "Volume" },
            { ("IEF", "10.11"), "Bedrooms" },
            { ("IEF", "10.13"), "Bedrooms" },
            { ("IEF", "11.1"),  "Blower Door" },
            { ("IEF", "11.16"), "Duct Leakage" },
            { ("IEF", "12.5"),  "Fresh Air" },
            { ("AFI", "1.4"),   "Returns" },
            { ("AFI", "1.5"),   "Floor Area" },
            { ("AFI", "2.1"),   "Design Airflow" },
            { ("AFI", "2.5"),   "Design Airflow" },
            { ("AFI", "3.1"),   "Design Airflow" },
            { ("AFI", "3.9"),   "Design Airflow" },
            { ("PLY", "2.1"),   "Type" },
            { ("IER", "2.1"),   "Type" },
            { ("ACI", "2.1"),   "Type" },
            { ("PS",  "2.1"),   "Type" },
        };

        internal static string NormalizeCode(string? code) => (code ?? "").ToUpperInvariant() switch
        {
            "HER"  => "IER",
            "QIER" => "IER",
            "HEF"  => "IEF",
            var c  => c
        };

        /// Returns the EC value mapped to a specific item, or null if no mapping exists.
        public static string? GetValueForItem(EnergyComplianceInfo info, string? inspCode, string? itemNum)
        {
            if (info == null || string.IsNullOrWhiteSpace(itemNum)) return null;
            string normCode = NormalizeCode(inspCode);
            if (!Mappings.TryGetValue((normCode, itemNum), out var getter)) return null;
            return getter(info);
        }

        /// Returns a short display label for the EC field mapped to a specific item.
        public static string? GetLabelForItem(string? inspCode, string? itemNum)
        {
            if (string.IsNullOrWhiteSpace(itemNum)) return null;
            string normCode = NormalizeCode(inspCode);
            return Labels.TryGetValue((normCode, itemNum), out var label) ? label : null;
        }

        /// Applies the EC value for the given item only. Returns true if the value was set.
        public static bool ApplySingleItem(EnergyComplianceInfo info, Item item, string? inspCode)
        {
            if (info == null || item == null) return false;
            string? value = GetValueForItem(info, inspCode, item.Number);
            if (string.IsNullOrWhiteSpace(value)) return false;
            return SetItemValue(item, value);
        }

        // ---------------------------------------------------------------
        // Public API
        // ---------------------------------------------------------------

        public static EnergyComplianceInfo GetInfoForInspection(string? insFilePath)
        {
            var info = new EnergyComplianceInfo();
            if (string.IsNullOrWhiteSpace(insFilePath) || !File.Exists(insFilePath))
            { info.StatusText = "No inspection file loaded."; return info; }

            string? jobFolder = GetJobFolder(insFilePath);
            if (string.IsNullOrWhiteSpace(jobFolder) || !Directory.Exists(jobFolder))
            { info.StatusText = "Job folder not found."; return info; }

            string engFolder = Path.Combine(jobFolder, "Engineering");
            if (!Directory.Exists(engFolder))
            { info.StatusText = "Engineering folder not found."; return info; }

            string jobId = Path.GetFileNameWithoutExtension(insFilePath).Split('-').FirstOrDefault() ?? "";
            string? ecPath = FindEcPdf(engFolder, jobId);
            if (ecPath == null)
            { info.StatusText = "No EC report found for this job."; return info; }

            info.PdfPath     = ecPath;
            info.DisplayName = Path.GetFileName(ecPath);

            try
            {
                ExtractFromPdf(ecPath, info);
                info.StatusText = info.IsLoaded ? "OK" : "EC report found but could not extract data.";
            }
            catch (Exception ex)
            {
                info.StatusText = $"Error reading EC report: {ex.Message}";
            }

            return info;
        }

        /// <summary>
        /// Applies EC data to the inspection, filling only empty items.
        /// Returns the number of items updated.
        /// </summary>
        public static int ApplyToInspection(EnergyComplianceInfo info, InspectionFile inspection)
        {
            if (!info.IsLoaded || inspection == null) return 0;

            string normCode = NormalizeCode(inspection.InspectionCode);

            // Build item-number → item map (first occurrence wins for duplicates)
            var byNum = inspection.Sections
                .SelectMany(s => s.Items)
                .GroupBy(i => i.Number ?? "")
                .ToDictionary(g => g.Key, g => g.First());

            int count = 0;
            foreach (var ((code, num), getter) in Mappings)
            {
                if (code != normCode) continue;
                string? ecValue = getter(info);
                if (string.IsNullOrWhiteSpace(ecValue)) continue;
                if (!byNum.TryGetValue(num, out var item)) continue;

                // Skip items that already have a value
                string? cur = item.Value?.ToString();
                if (!string.IsNullOrWhiteSpace(cur)) continue;

                if (SetItemValue(item, ecValue)) count++;
            }
            return count;
        }

        /// <summary>
        /// Applies slab engineering data to a CPP inspection, filling empty measurement items.
        /// Returns the number of items updated.
        /// </summary>
        internal static int ApplyCppSlabToInspection(SlabEngineeringInfo slab, InspectionFile inspection)
        {
            if (slab == null || inspection == null) return 0;

            var byNum = inspection.Sections
                .SelectMany(s => s.Items)
                .GroupBy(i => i.Number ?? "")
                .ToDictionary(g => g.Key, g => g.First());

            int count = 0;

            bool TrySet(string num, string? value)
            {
                if (string.IsNullOrWhiteSpace(value)) return false;
                if (!byNum.TryGetValue(num, out var item)) return false;
                if (!string.IsNullOrWhiteSpace(item.Value?.ToString())) return false;
                item.Value = value;
                return true;
            }

            string? bwStr = slab.BeamWidthInches.HasValue ? slab.BeamWidthInches.ToString() : null;
            string? bdStr = slab.BeamDepthInches.HasValue ? slab.BeamDepthInches.ToString() : null;

            // TOF-to-BOB at corners = beam depth only
            // (TOF-to-TOG items 8.1/8.4/8.7/… are omitted — not derivable from plan data)
            foreach (var n in new[] { "8.2","8.5","8.8","8.11" })
                if (TrySet(n, bdStr)) count++;

            // BW at corners + interior = beam width
            foreach (var n in new[] { "8.3","8.6","8.9","8.12","8.14","8.17","8.20","8.23" })
                if (TrySet(n, bwStr)) count++;

            // BD at interior = beam depth
            foreach (var n in new[] { "8.15","8.18","8.21","8.24" })
                if (TrySet(n, bdStr)) count++;

            // Cable count total → note in comment on item 5.1.b
            if (slab.CableCount.HasValue && byNum.TryGetValue("5.1.b", out var cableItem))
            {
                if (string.IsNullOrWhiteSpace(cableItem.Comments))
                {
                    cableItem.Comments = $"Plan total: {slab.CableCount} strands";
                    count++;
                }
            }

            return count;
        }

        // ---------------------------------------------------------------
        // Slab — surgical single-item apply
        // ---------------------------------------------------------------

        private static readonly Dictionary<string, Func<SlabEngineeringInfo, string?>> SlabMappings = new()
        {
            // Foundation type inferred from cable presence
            { "2.0",  s => s.CableCount.HasValue ? (s.CableCount.Value > 0 ? "Post Tensioned" : "Conventionally Reinforced") : null },

            // TOF-to-BOB at corners = beam depth only
            // (TOF is at the bottom of the beam; BOB is also the beam bottom — so this is just beam depth)
            { "8.2",  s => s.BeamDepthInches?.ToString() },
            { "8.5",  s => s.BeamDepthInches?.ToString() },
            { "8.8",  s => s.BeamDepthInches?.ToString() },
            { "8.11", s => s.BeamDepthInches?.ToString() },

            // Beam width
            { "8.3",  s => s.BeamWidthInches?.ToString() },
            { "8.6",  s => s.BeamWidthInches?.ToString() },
            { "8.9",  s => s.BeamWidthInches?.ToString() },
            { "8.12", s => s.BeamWidthInches?.ToString() },
            { "8.14", s => s.BeamWidthInches?.ToString() },
            { "8.17", s => s.BeamWidthInches?.ToString() },
            { "8.20", s => s.BeamWidthInches?.ToString() },
            { "8.23", s => s.BeamWidthInches?.ToString() },

            // Beam depth
            { "8.15", s => s.BeamDepthInches?.ToString() },
            { "8.18", s => s.BeamDepthInches?.ToString() },
            { "8.21", s => s.BeamDepthInches?.ToString() },
            { "8.24", s => s.BeamDepthInches?.ToString() },

            // NOTE: TOF-to-TOG items (8.1, 8.4, 8.7, 8.10, 8.13, 8.16, 8.19, 8.22) are intentionally
            // omitted — that measurement is not derivable from plan data.
        };

        private static readonly Dictionary<string, string> SlabLabels = new()
        {
            { "2.0",  "Foundation" },
            { "8.2",  "TOF-BOB" }, { "8.5",  "TOF-BOB" }, { "8.8",  "TOF-BOB" }, { "8.11", "TOF-BOB" },
            { "8.3",  "Beam W" },  { "8.6",  "Beam W" },  { "8.9",  "Beam W" },
            { "8.12", "Beam W" },  { "8.14", "Beam W" },  { "8.17", "Beam W" },
            { "8.20", "Beam W" },  { "8.23", "Beam W" },
            { "8.15", "Beam D" },  { "8.18", "Beam D" },  { "8.21", "Beam D" },  { "8.24", "Beam D" },
        };

        internal static string? GetSlabValueForItem(SlabEngineeringInfo slab, string? itemNum)
        {
            if (slab == null || itemNum == null) return null;
            return SlabMappings.TryGetValue(itemNum, out var getter) ? getter(slab) : null;
        }

        public static string? GetSlabLabelForItem(string? itemNum)
        {
            if (itemNum == null) return null;
            return SlabLabels.TryGetValue(itemNum, out var label) ? label : null;
        }

        /// <summary>
        /// Applies slab data to a single item. Returns true if the item was updated.
        /// </summary>
        internal static bool ApplySlabToSingleItem(SlabEngineeringInfo slab, Item item)
        {
            if (slab == null || item == null) return false;
            string? value = GetSlabValueForItem(slab, item.Number);
            if (string.IsNullOrWhiteSpace(value)) return false;
            item.Value = value;
            return true;
        }

        // ---------------------------------------------------------------
        // Banner state — compares design values from EC/slab PDF against
        // what the inspector has already entered in the item.
        // ---------------------------------------------------------------

        public enum BannerState { Gray, Green, Red }

        private enum EcCompareType { AtMost, AtLeast, ExactOrClose, TextMatch }

        // Comparison direction per (normalized code, item number)
        private static readonly Dictionary<(string code, string num), EcCompareType> EcComparisons = new()
        {
            // AtMost: actual ≤ design is good (CFM limits, max U/SHGC)
            { ("HET", "1.1"),   EcCompareType.AtMost },   // DuctLeakage max
            { ("HET", "1.3"),   EcCompareType.AtMost },   // BlowerDoor max
            { ("HET", "5.6"),   EcCompareType.AtMost },   // VentFanWatts max
            { ("IER", "2.2"),   EcCompareType.ExactOrClose }, // U-factor — must match design spec
            { ("IER", "2.3"),   EcCompareType.ExactOrClose }, // SHGC — must match design spec
            // AtLeast: actual ≥ design is good (min R-values, SEER, fresh air)
            { ("IER", "3.2"),   EcCompareType.AtLeast },  // SlopedCeilingR min
            { ("IER", "3.3"),   EcCompareType.AtLeast },  // WallR min
            { ("IER", "8.1"),   EcCompareType.AtLeast },  // SupplyDuctR min
            { ("IER", "8.2"),   EcCompareType.AtLeast },  // ReturnDuctR min
            { ("IER", "9.1"),   EcCompareType.AtLeast },  // HotWaterPipeR min
            { ("IER", "11.1"),  EcCompareType.AtMost },   // Duct leakage max CFM
            { ("IER", "12.1"),  EcCompareType.AtLeast },  // AtticWallR min
            { ("IEF", "4.2"),   EcCompareType.AtLeast },  // SEER min
            { ("IEF", "8.2"),   EcCompareType.AtLeast },  // AtticCeilingR min
            { ("HET", "1.6"),   EcCompareType.AtLeast },  // FreshAir min CFM
            { ("IEF", "12.5"),  EcCompareType.AtLeast },  // FreshAir min CFM
            // ExactOrClose: must match (±5% for large values, integer-exact for small)
            { ("IER", "10.2"),  EcCompareType.ExactOrClose }, // FloorArea
            { ("HET", "1.4"),   EcCompareType.ExactOrClose }, // FloorArea
            { ("HET", "1.5"),   EcCompareType.ExactOrClose }, // Volume
            { ("HET", "2.12"),  EcCompareType.AtMost },       // BlowerDoor max @ 50 Pa
            { ("HET", "3.1"),   EcCompareType.ExactOrClose }, // Returns
            { ("HET", "3.3"),   EcCompareType.AtMost },       // Duct leakage max
            { ("HET", "3.4"),   EcCompareType.AtMost },       // Duct leakage max
            { ("HET", "3.5"),   EcCompareType.AtMost },       // Duct leakage max
            { ("HET", "3.6"),   EcCompareType.AtMost },       // Duct leakage max
            { ("IEF", "10.8"),  EcCompareType.ExactOrClose }, // FloorArea
            { ("IEF", "10.9"),  EcCompareType.ExactOrClose }, // Volume
            { ("IEF", "10.11"), EcCompareType.ExactOrClose }, // Bedrooms (older form)
            { ("IEF", "10.13"), EcCompareType.ExactOrClose }, // Bedrooms (newer form)
            { ("IEF", "11.1"),  EcCompareType.AtMost },       // BlowerDoor max
            { ("IEF", "11.16"), EcCompareType.AtMost },       // DuctLeakage max
            { ("AFI", "1.4"),   EcCompareType.ExactOrClose }, // Returns
            { ("AFI", "1.5"),   EcCompareType.ExactOrClose }, // FloorArea
            { ("AFI", "2.1"),   EcCompareType.ExactOrClose }, // DesignAirflow
            { ("AFI", "2.5"),   EcCompareType.ExactOrClose }, // DesignAirflow
            { ("AFI", "3.1"),   EcCompareType.ExactOrClose }, // DesignAirflow
            { ("AFI", "3.9"),   EcCompareType.ExactOrClose }, // DesignAirflow
            // TextMatch: loose text contains comparison
            { ("IER", "1.3"),   EcCompareType.TextMatch },    // EnergyStarProgram
            { ("IER", "1.4"),   EcCompareType.TextMatch },    // IECC code lookup value
            { ("HET", "1.7"),   EcCompareType.TextMatch },    // RunTime (text)
            { ("IEF", "3.4"),   EcCompareType.TextMatch },    // WaterHeaterFuel
            { ("IEF", "3.5"),   EcCompareType.TextMatch },    // WaterHeaterCapacity
            // Performance path selection — always "Performance IECC" when EC report exists
            { ("IER", "2.1"),   EcCompareType.TextMatch },
            { ("PLY", "2.1"),   EcCompareType.TextMatch },
            { ("ACI", "2.1"),   EcCompareType.TextMatch },
            { ("PS",  "2.1"),   EcCompareType.TextMatch },
        };

        // Sets of slab item numbers by measurement type
        private static readonly HashSet<string> BwItems = new()
            { "8.3","8.6","8.9","8.12","8.14","8.17","8.20","8.23" };
        private static readonly HashSet<string> BdItems = new()
            { "8.2","8.5","8.8","8.11","8.15","8.18","8.21","8.24" };

        /// <summary>
        /// Extract a leading number from strings like "R6", "R13", "15.2 SEER2", "2,199 sq ft".
        /// Returns null if no number found.
        /// </summary>
        private static double? ParseNumericValue(string? s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            // Strip R prefix: "R6" → "6"
            string t = Regex.Replace(s.Trim(), @"^R(?=\d)", "", RegexOptions.IgnoreCase);
            var mixedFraction = Regex.Match(t, @"^(-?[\d,]+)\s+(\d+)\s*/\s*(\d+)");
            if (mixedFraction.Success &&
                double.TryParse(mixedFraction.Groups[1].Value.Replace(",", ""),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double whole) &&
                double.TryParse(mixedFraction.Groups[2].Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double numerator) &&
                double.TryParse(mixedFraction.Groups[3].Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double denominator) &&
                Math.Abs(denominator) > 0.0001)
            {
                return whole + numerator / denominator;
            }

            var simpleFraction = Regex.Match(t, @"^(-?\d+)\s*/\s*(\d+)");
            if (simpleFraction.Success &&
                double.TryParse(simpleFraction.Groups[1].Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out numerator) &&
                double.TryParse(simpleFraction.Groups[2].Value,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out denominator) &&
                Math.Abs(denominator) > 0.0001)
            {
                return numerator / denominator;
            }
            // Extract leading number (handles commas, decimals)
            var m = Regex.Match(t, @"^[\d,]+\.?\d*");
            if (!m.Success)
            {
                // Handle labeled values like "Wall: R13" or "Ceiling: R19".
                m = Regex.Match(t, @"R\s*([\d,]+\.?\d*)", RegexOptions.IgnoreCase);
                if (m.Success)
                    return double.TryParse(m.Groups[1].Value.Replace(",", ""),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double rValue) ? rValue : null;

                // Last-resort labeled number, e.g. "Design Airflow: 760".
                m = Regex.Match(t, @"[\d,]+\.?\d*");
                if (!m.Success) return null;
            }
            return double.TryParse(m.Value.Replace(",", ""),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double d) ? d : null;
        }

        private static bool TextMatches(string actual, string design)
        {
            string a = actual.Trim().ToLowerInvariant();
            string d = design.Trim().ToLowerInvariant();
            return a.Contains(d) || d.Contains(a);
        }

        private static BannerState NumericCompare(string actual, string design, Func<double, double, bool> ok)
        {
            double? av = ParseNumericValue(actual);
            double? dv = ParseNumericValue(design);
            if (av == null || dv == null)
                return TextMatches(actual, design) ? BannerState.Green : BannerState.Red;
            return ok(av.Value, dv.Value) ? BannerState.Green : BannerState.Red;
        }

        private static BannerState NumericOrTextClose(string actual, string design)
        {
            double? av = ParseNumericValue(actual);
            double? dv = ParseNumericValue(design);
            if (av != null && dv != null)
            {
                // Large values (> 100): within 5%; mid values (10–100): ±0.5; small values (≤ 10, e.g. U-factor, SHGC): 5%
                double tol = dv.Value > 100 ? dv.Value * 0.05 :
                             dv.Value > 10  ? 0.5 :
                             dv.Value * 0.05;
                return Math.Abs(av.Value - dv.Value) <= tol ? BannerState.Green : BannerState.Red;
            }
            return TextMatches(actual, design) ? BannerState.Green : BannerState.Red;
        }

        /// <summary>
        /// Returns the banner state for the current EC item.
        /// Gray  = no design data mapped to this item.
        /// Green = design value exists and item matches within tolerance.
        /// Red   = design value exists but item is blank OR outside acceptable range.
        /// </summary>
        public static BannerState GetEcItemBannerState(EnergyComplianceInfo info, string? inspCode, string? itemNum, string? actualValue)
        {
            if (info == null || !info.IsLoaded) return BannerState.Gray;
            string? designValue = GetValueForItem(info, inspCode, itemNum);
            if (string.IsNullOrWhiteSpace(designValue)) return BannerState.Gray;
            if (string.IsNullOrWhiteSpace(actualValue)) return BannerState.Red; // blank ≠ design value

            string normCode = NormalizeCode(inspCode);
            if (!EcComparisons.TryGetValue((normCode, itemNum!), out var compareType))
                return BannerState.Gray; // no comparison defined

            return compareType switch
            {
                EcCompareType.TextMatch    => TextMatches(actualValue, designValue) ? BannerState.Green : BannerState.Red,
                EcCompareType.AtMost       => NumericCompare(actualValue, designValue, (a, d) => a <= d),
                EcCompareType.AtLeast      => NumericCompare(actualValue, designValue, (a, d) => a >= d),
                EcCompareType.ExactOrClose => NumericOrTextClose(actualValue, designValue),
                _                          => BannerState.Gray,
            };
        }

        /// <summary>
        /// Returns the banner state for the current slab CPP item.
        /// cableF2B / cableR2L: inspector-entered values from items 5.1.b and 5.1.c.
        /// Beam width: green if [design_min, 18"]. Beam depth: green if [design_min, design_min+12"].
        /// Cable count: green when both are filled and F2B + S2S == design total. Red otherwise.
        /// </summary>
        internal static BannerState GetSlabItemBannerState(SlabEngineeringInfo slab, string? itemNum, string? actualValue, int? cableF2B = null, int? cableR2L = null)
        {
            if (slab == null || itemNum == null) return BannerState.Gray;

            // Cable count items 5.1.b (F2B) and 5.1.c (S2S)
            if (itemNum == "5.1.b" || itemNum == "5.1.c")
            {
                if (!slab.CableCount.HasValue) return BannerState.Gray;
                // Incomplete or wrong sum = red; only green when both are filled and sum matches
                if (cableF2B == null || cableR2L == null) return BannerState.Red;
                return (cableF2B.Value + cableR2L.Value) == slab.CableCount.Value
                    ? BannerState.Green : BannerState.Red;
            }

            string? designValue = GetSlabValueForItem(slab, itemNum);
            if (string.IsNullOrWhiteSpace(designValue)) return BannerState.Gray;
            if (string.IsNullOrWhiteSpace(actualValue)) return BannerState.Red; // blank ≠ design value

            // Foundation type: exact text match
            if (itemNum == "2.0")
                return actualValue.Trim().Equals(designValue.Trim(), StringComparison.OrdinalIgnoreCase)
                    ? BannerState.Green : BannerState.Red;

            if (!BwItems.Contains(itemNum) && !BdItems.Contains(itemNum))
                return BannerState.Gray;

            double? actualParsed = ParseNumericValue(actualValue);
            double? designParsed = ParseNumericValue(designValue);
            if (actualParsed == null || designParsed == null) return BannerState.Gray;
            double actual = actualParsed.Value;
            double design = designParsed.Value;

            if (BwItems.Contains(itemNum))
                // Beam width: [min, 18"] — wider than 18" is suspicious, narrower than min fails
                return (actual >= design && actual <= 18.0) ? BannerState.Green : BannerState.Red;
            else
                // Beam depth: [min, min+12"] — more than 12" over min suggests dig error
                return (actual >= design && actual <= design + 12.0) ? BannerState.Green : BannerState.Red;
        }

        // ---------------------------------------------------------------
        // PDF discovery
        // ---------------------------------------------------------------

        public static string? FindEcPdf(string engFolder, string jobId)
        {
            if (!Directory.Exists(engFolder) || string.IsNullOrWhiteSpace(jobId))
                return null;

            var candidates = Directory.GetFiles(engFolder, "*.pdf")
                .Select(path => new
                {
                    Path = path,
                    Match = Regex.Match(
                        Path.GetFileNameWithoutExtension(path),
                        $@"^{Regex.Escape(jobId)}(?:R(?<rev>\d+))?EC$",
                        RegexOptions.IgnoreCase)
                })
                .Where(x => x.Match.Success)
                .Select(x => new
                {
                    x.Path,
                    Revision = int.TryParse(x.Match.Groups["rev"].Value, out int rev) ? rev : 0
                })
                .OrderByDescending(x => x.Revision)
                .ThenBy(x => x.Path)
                .ToList();

            return candidates.FirstOrDefault()?.Path;
        }

        private static string? GetJobFolder(string insFilePath)
        {
            string? insFolder   = Path.GetDirectoryName(insFilePath);
            string? insRoot     = insFolder != null ? Path.GetDirectoryName(insFolder) : null;
            string  jobsFolder  = insRoot  != null ? Path.Combine(insRoot, "Jobs") : "";
            string  jobId       = Path.GetFileNameWithoutExtension(insFilePath).Split('-').FirstOrDefault() ?? "";
            if (string.IsNullOrWhiteSpace(jobsFolder) || string.IsNullOrWhiteSpace(jobId)) return null;
            return Path.Combine(jobsFolder, jobId);
        }

        // ---------------------------------------------------------------
        // Text extraction + parsing
        // ---------------------------------------------------------------

        public static readonly string DebugTextPath = Path.Combine(Path.GetTempPath(), "red_ec_debug.txt");

        private static void ExtractFromPdf(string pdfPath, EnergyComplianceInfo info)
        {
            // 1. Try PdfPig text extraction (fast, works on computer-generated PDFs)
            var sb = new StringBuilder();
            using var doc = PdfDocument.Open(pdfPath);
            foreach (var page in doc.GetPages())
                sb.AppendLine(page.Text);
            string raw = sb.ToString();

            // 2. If PdfPig returned nothing meaningful, fall back to Tesseract OCR
            if (raw.Trim().Length < 50)
            {
                string? tessData = GetTessDataPath();
                if (tessData != null)
                    raw = OcrAllPages(pdfPath, tessData);
            }

            try { File.WriteAllText(DebugTextPath, $"PDF: {pdfPath}\n\n{raw}"); } catch { }
            ParseText(raw, info);
        }

        private static string OcrAllPages(string pdfPath, string tessDataPath)
        {
            var sb = new StringBuilder();
            try
            {
                using var engine = new TesseractEngine(tessDataPath, "eng", EngineMode.Default);
                using var docReader = DocLib.Instance.GetDocReader(pdfPath, new PageDimensions(3.0));
                int pageCount = docReader.GetPageCount();
                for (int i = 0; i < pageCount; i++)
                {
                    using var pageReader = docReader.GetPageReader(i);
                    byte[] rawBytes = pageReader.GetImage();
                    int w = pageReader.GetPageWidth();
                    int h = pageReader.GetPageHeight();
                    sb.AppendLine(OcrBytesEc(rawBytes, w, h, engine));
                    sb.AppendLine("---PAGE_BREAK---");
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"EC OCR failed: {ex.Message}");
            }
            return sb.ToString();
        }

        private static string OcrBytesEc(byte[] bgraBytes, int width, int height, TesseractEngine engine)
        {
            try
            {
                using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                var bmpData = bmp.LockBits(new Rectangle(0, 0, width, height),
                    ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                Marshal.Copy(bgraBytes, 0, bmpData.Scan0, bgraBytes.Length);
                bmp.UnlockBits(bmpData);
                using var ms = new MemoryStream();
                bmp.Save(ms, DrawingImageFormat.Png);
                using var pix = Pix.LoadFromMemory(ms.ToArray());
                using var page = engine.Process(pix);
                return page.GetText() ?? "";
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"EC OCR page error: {ex.Message}");
                return "";
            }
        }

        private static string? _tessDataPath;
        private static bool _tessDataPathResolved;
        private static string? GetTessDataPath()
        {
            if (_tessDataPathResolved) return _tessDataPath;
            _tessDataPathResolved = true;
            string appDir = AppDomain.CurrentDomain.BaseDirectory;
            var candidates = new[]
            {
                Path.Combine(appDir, "tessdata"),
                Path.Combine(appDir, "..", "tessdata"),
                @"C:\Program Files\Tesseract-OCR\tessdata",
                @"C:\Program Files (x86)\Tesseract-OCR\tessdata",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "Tesseract-OCR", "tessdata"),
            };
            _tessDataPath = candidates.FirstOrDefault(p =>
                Directory.Exists(p) && File.Exists(Path.Combine(p, "eng.traineddata")));
            return _tessDataPath;
        }

        private static void ParseText(string raw, EnergyComplianceInfo info)
        {
            string text = raw.Replace("\r\n", "\n").Replace("\r", "\n");

            // HERS Index
            info.HersIndex = First(text,
                @"As Designed Home ERI \(HERS\)[:\s]*(\d+)",
                @"HERS Index Score[:\s]*(\d+)",
                @"ERI[:\s]+(\d{2,3})(?!\d)");

            // Conditioned Floor Area — handles both "... sq ft: 2,218" and "[sq. ft.]: 2,218" formats
            info.ConditionedFloorArea = CleanNum(First(text,
                @"Conditioned Floor Area\s*\[sq\.?\s*ft\.?\]\s*[:\s]*([\d,]+)",
                @"Conditioned Floor Area[:\s]*([\d,]+)\s*sq",
                @"Conditioned Floor Area[:\s]*([\d,]+)"));
            // Sanity check: a house floor area must be ≥ 500 sq ft
            if (info.ConditionedFloorArea != null)
            {
                string faRaw = info.ConditionedFloorArea.Replace(",", "");
                if (!int.TryParse(faRaw, out int faInt) || faInt < 500)
                    info.ConditionedFloorArea = null;
            }

            // Conditioned Volume — value may be on same line or next line (table extraction)
            info.ConditionedVolume = CleanNum(First(text,
                @"Conditioned Volume\s*\[cu\.?\s*ft\.?\][^\d\r\n]*\r?\n?\s*([\d,]+)",
                @"Conditioned Volume[^\d\r\n]*\r?\n?\s*([\d,]+)\s*cu",
                @"Conditioned Volume[^\d\r\n]*\r?\n?\s*([\d,]+)"));

            // Number of Bedrooms — labeled or bare number above square footage
            info.NumberOfBedrooms = First(text,
                @"Number of Bedrooms[:\s]*(\d+)",
                @"Bedrooms?[:\s]+(\d+)",
                @"(\d+)\s*Bedrooms?");

            // Positional fallback: EC column-format reports show a lone 1-9 digit
            // on the line immediately above the floor area value.
            if (info.NumberOfBedrooms == null && info.ConditionedFloorArea != null)
            {
                // Find the floor area in the raw text, then scan backwards for a lone small integer
                var faMatch = Regex.Match(text, Regex.Escape(info.ConditionedFloorArea));
                if (!faMatch.Success)
                    faMatch = Regex.Match(text, Regex.Escape(info.ConditionedFloorArea.Replace(",", "")));
                if (faMatch.Success)
                {
                    int lookback = Math.Min(faMatch.Index, 300);
                    string before = text.Substring(faMatch.Index - lookback, lookback);
                    // Last lone digit 1-9 in that window (not part of a larger number)
                    var bdMatch = Regex.Match(before, @"(?<![.\d])([1-9])(?![.\d])\s*$");
                    if (!bdMatch.Success)
                        bdMatch = Regex.Match(before, @"(?<![.\d])([1-9])(?![.\d])\s*\n\s*$");
                    if (bdMatch.Success)
                        info.NumberOfBedrooms = bdMatch.Groups[1].Value;
                }
            }

            // Blower Door CFM @ 50 Pa
            // Handles "CFM @ 50 Pa", "CFM at 50 Pa" (Ekotrope detail reports), "CFM50" (IECC label).
            // \s* in these patterns also matches newlines (\n) in .NET regex, so column-extracted text is covered.
            string? bdCfm = First(text,
                @"([\d,.]+)\s*CFM\s*@\s*50\s*Pa",
                @"([\d,.]+)\s*CFM\s*at\s*50\s*Pa",
                @"([\d,.]+)\s*CFM@50Pa",
                @"Infiltration[:\s]*([\d,.]+)\s*CFM50",
                @"Blower[- ]?Door[:\s]+([\d,.]+)\s*CFM",
                // Ekotrope "Whole House Infiltration" table: value may appear after section header
                @"Whole\s+House\s+Infiltration[\s\S]{0,300}?([\d,.]+)\s*CFM",
                // OCR may drop "CFM" and leave bare number near "50 Pa"
                @"([\d,.]+)\s+at\s+50\s+Pa",
                @"([\d,.]+)\s+50\s+Pa\b");
            if (bdCfm != null)
            {
                // Sanity check: a house blower door target is typically 100–5000 CFM
                if (double.TryParse(bdCfm.Replace(",", ""), out double bdCheck) && bdCheck >= 50 && bdCheck <= 10000)
                    info.BlowerDoorMaxCfm = CleanNum(bdCfm);
            }
            if (info.BlowerDoorMaxCfm == null)
            {
                // Fallback: ACH × Volume / 60
                string? ach = First(text,
                    @"([\d.]+)\s*ACH\s*at\s*50\s*Pa",
                    @"([\d.]+)\s*ACH50");
                if (ach != null && info.ConditionedVolume != null
                    && double.TryParse(ach, out double achVal)
                    && double.TryParse(info.ConditionedVolume.Replace(",",""), out double volVal)
                    && achVal > 0 && achVal < 30)
                {
                    info.BlowerDoorMaxCfm = ((int)Math.Round(achVal * volVal / 60.0)).ToString();
                }
            }

            // Total Duct Leakage CFM @ 25 Pa
            // Also handles IECC label: "Duct Lkg to Outdoors: 95 CFM @ 25Pa"
            info.DuctLeakageMaxCfm = CleanNum(First(text,
                @"Duct Leakage[:\s]*([\d,]+\.?\d*)\s*CFM\s*@\s*25",
                @"Total Duct Leakage[:\s]*([\d,]+\.?\d*)\s*CFM",
                @"Duct Lkg to Outdoors[:\s]*([\d,]+\.?\d*)\s*CFM",
                @"([\d,]+\.?\d*)\s*CFM\s*@\s*25\s*Pa"));

            // Return grilles — several pattern strategies to handle different PDF extraction orderings:
            // (a) same-line: label and value on one line (most common for digital PDFs)
            // (b) next-line: value on the line immediately following the label
            // (c) reversed: value on the line immediately before the next label (column-by-column extraction)
            // (d) wide: value anywhere within 400 chars, excluding N-in-parentheses and N,NNN patterns
            info.NumberOfReturns = First(text,
                // Tight: label then whitespace-only then digit (avoids catching (1) in equipment names)
                @"#\s*Return Grilles\s+([1-9]\d?)\b",
                @"Return Grilles:?\s+([1-9]\d?)\b",
                @"Number of Returns:?\s+([1-9]\d?)\b",
                @"Return Air Locations:?\s+([1-9]\d?)\b",
                // Next-line: value on line immediately after label
                @"#\s*Return Grilles[^\d\r\n]*\r?\n\s*([1-9]\d?)\b",
                @"Return Grilles[^\d\r\n]*\r?\n\s*([1-9]\d?)\b",
                // Reversed: value on the line just before "Supply Duct R Value" label
                @"([1-9]\d?)\s*\r?\n\s*Supply Duct R[\s-]?Value");
                // NOTE: The former "wide" fuzzy pattern was removed — it caused false matches on
                // "1" from nearby text in column-by-column PDFs. The multi-capture fallback below
                // handles the Ekotrope column-by-column layout reliably.

            // Duct R-values — same four strategies.
            // [\s-]? between R and Value handles "R Value" (space), "R-Value" (hyphen), R\u00A0Value (NBSP).
            string? supR = First(text,
                @"Supply Duct R[\s-]?Value[^\n]*?(\d+)",                  // (a) same line
                @"Supply Duct R[\s-]?Value[^\d\r\n]*\r?\n\s*(\d+)",       // (b) next line
                @"(\d+)\s*\r?\n\s*Return Duct R[\s-]?Value",              // (c) reversed
                @"Duct Insulation[^\n]*Supply[:\s]*R-?(\d+)",              // IECC label format
                @"Supply[:\s]+R-?(\d+)");                                  // fallback
            if (supR != null) info.SupplyDuctR = $"R{supR}";

            string? retR = First(text,
                @"Return Duct R[\s-]?Value[^\n]*?(\d+)",                  // (a) same line
                @"Return Duct R[\s-]?Value[^\d\r\n]*\r?\n\s*(\d+)",       // (b) next line
                @"(\d+)\s*\r?\n\s*Supply Duct Area",                       // (c) reversed
                @"Duct Insulation[^\n]*Return[:\s]*R-?(\d+)",              // IECC label format
                @"Return[:\s]+R-?(\d+)");                                  // fallback
            if (retR != null) info.ReturnDuctR = $"R{retR}";

            // Ekotrope column-by-column fallback: PdfPig extracts labels block then values block.
            // Values appear in sequence: [floor area (4+ chars with comma)] → [return grilles (1-9)]
            // → [supply duct R (1-2 digits)] → [return duct R (1-2 digits)] → [duct area (digit…)]
            if (info.NumberOfReturns == null || info.SupplyDuctR == null || info.ReturnDuctR == null)
            {
                var dm = Regex.Match(text,
                    @"([\d,]{4,})\s*\n+\s*([1-9]\d?)(?!\d)\s*\n+\s*(\d{1,2})(?!\d|\.)\s*\n+\s*(\d{1,2})(?!\d|\.)\s*\n+\s*\d",
                    RegexOptions.None);
                if (dm.Success)
                {
                    if (info.NumberOfReturns == null) info.NumberOfReturns = dm.Groups[2].Value;
                    if (info.SupplyDuctR == null)     info.SupplyDuctR = $"R{dm.Groups[3].Value}";
                    if (info.ReturnDuctR == null)     info.ReturnDuctR = $"R{dm.Groups[4].Value}";
                }
            }

            // Window U-factor — most common value across all glazing
            // IECC label uses "U-Value: 0.34" rather than "U-factor"
            var uVals = Regex.Matches(text, @"U-(?:factor|value)[:\s]*([\d.]+)", RegexOptions.IgnoreCase)
                .Cast<Match>()
                .Select(m => m.Groups[1].Value)
                .Where(v => double.TryParse(v, out double d) && d > 0.1 && d < 2.0)
                .GroupBy(v => v).OrderByDescending(g => g.Count())
                .Select(g => g.Key).ToList();
            info.WindowUFactor = uVals.FirstOrDefault();

            // Window SHGC — most common
            var shVals = Regex.Matches(text, @"SHGC\s*([\d.]+)", RegexOptions.IgnoreCase)
                .Cast<Match>()
                .Select(m => m.Groups[1].Value)
                .Where(v => double.TryParse(v, out double d) && d > 0.01 && d < 1.0)
                .GroupBy(v => v).OrderByDescending(g => g.Count())
                .Select(g => g.Key).ToList();
            info.WindowSHGC = shVals.FirstOrDefault();

            // Integer "34/22 (P)" notation used in ESTAR calc sheets:
            //   NN/NN where first = U-factor × 100, second = SHGC × 100
            // Try with explicit (P) suffix first (most specific); then near "Windows"/"Glazing"
            if (info.WindowUFactor == null || info.WindowSHGC == null)
            {
                Match slashM = Regex.Match(text, @"\b(\d{2,3})/(\d{2,3})\s*\(P\)", RegexOptions.IgnoreCase);
                if (!slashM.Success)
                    slashM = Regex.Match(text, @"Windows?\b[^\n]{0,80}?\b(\d{2,3})/(\d{2,3})\b", RegexOptions.IgnoreCase);
                if (!slashM.Success)
                    slashM = Regex.Match(text, @"Glazing[^\n]{0,80}?\b(\d{2,3})/(\d{2,3})\b", RegexOptions.IgnoreCase);
                if (slashM.Success
                    && int.TryParse(slashM.Groups[1].Value, out int uInt)
                    && int.TryParse(slashM.Groups[2].Value, out int shInt)
                    && uInt >= 10 && uInt <= 120
                    && shInt >= 5 && shInt <= 95)
                {
                    if (info.WindowUFactor == null) info.WindowUFactor = (uInt / 100.0).ToString("0.00");
                    if (info.WindowSHGC == null)    info.WindowSHGC    = (shInt / 100.0).ToString("0.00");
                }
            }

            // Foam-encapsulated attic wall R
            string? awNum = First(text,
                @"Attic Wall[^\n]*R-?(\d+)",
                @"2x4 R-?(\d+)[^\n]*FOAM[^\n]*Attic");
            if (awNum != null)
            {
                bool awFoam = Regex.IsMatch(text, @"Attic Wall[^\n]*FOAM", RegexOptions.IgnoreCase)
                           || Regex.IsMatch(text, @"FOAM[^\n]*Attic Wall", RegexOptions.IgnoreCase);
                string? awThk = awFoam ? InchThickness(text, "Attic Wall") : null;
                info.AtticWallR = awFoam ? (awThk != null ? $"R{awNum} {awThk} FOAM" : $"R{awNum} FOAM") : $"R{awNum}";
            }

            // Foam-encapsulated roof deck R
            string? arNum = First(text,
                @"Roof Deck[^\n]*R-?(\d+)",
                @"R-?(\d+)-?ROOF");
            if (arNum != null)
            {
                string? arThk = InchThickness(text, "Roof Deck");
                info.AtticRoofR = arThk != null ? $"R{arNum} {arThk} FOAM" : $"R{arNum} FOAM";
            }

            // Sloped ceiling R (cathedral / spray-foam rafter bays)
            // OCR table rows often wrap: "Slope" on one line, "Vaulted R-22" on the next.
            string? slopeNum = First(text,
                @"Vaulted\s+R-?(\d+)",           // wrapped row: "3.1 Vaulted R-22"
                @"Slope[:\s]+[^\n]*R-?(\d+)",    // single-line: "Slope: R22"
                @"Sloped Ceiling[^\n]*R-?(\d+)", // "Sloped Ceiling R22"
                @"Slope[^\n]{0,5}\n[^\n]*R-?(\d+)"); // "Slope\n...R-22" across newline
            if (slopeNum != null)
            {
                string? sThk = InchThickness(text, "Slope");
                info.SlopedCeilingR = sThk != null ? $"R{slopeNum} {sThk} FOAM" : $"R{slopeNum}";
            }
            else if (arNum != null)
            {
                // All-foam attic — roof deck R covers sloped portion
                info.SlopedCeilingR = info.AtticRoofR;
            }

            // Vented-attic ceiling (flat, blown/batt) → IEF 8.2
            string? flatNum = First(text,
                @"Flat[:\s]+[^\n]*R-?(\d+)",
                @"Ceiling[^\n]*R-?(\d+)(?!\s*FOAM)",
                @"R-?(\d+)\s+blown",
                @"R-?(\d+)\s+batt");
            if (flatNum != null)
                info.AtticCeilingR = $"R{flatNum}";
            else if (info.AtticRoofR == null && slopeNum != null)
                info.AtticCeilingR = $"R{slopeNum}";

            // Exterior wall R — collect ALL wall entries; majority rules for applied value.
            // Also handles IECC label: "Above Grade Walls: R-13"
            var wallGroups = Regex.Matches(text,
                    @"(?:Siding|Wood Frame Wall|Brick Wall|Above Grade Walls?)[^\n]*R-?(\d+)",
                    RegexOptions.IgnoreCase)
                .Cast<Match>()
                .Select(m => m.Groups[1].Value)
                .Where(v => int.TryParse(v, out int r) && r >= 11 && r <= 30)
                .GroupBy(v => v)
                .OrderByDescending(g => g.Count())
                .ToList();
            if (wallGroups.Count > 0)
            {
                info.WallR = $"R{wallGroups[0].Key}";
                info.WallRDetails = string.Join(", ", wallGroups.Select(g => $"R{g.Key} ×{g.Count()}"));
            }

            // Radiant barrier
            bool hasRB = Regex.IsMatch(text, @"\bRadiant Barrier\b", RegexOptions.IgnoreCase);
            bool rbNo  = Regex.IsMatch(text, @"Radiant Barrier[:\s]*No", RegexOptions.IgnoreCase);
            if (hasRB) info.RadiantBarrier = !rbNo;

            // Hot water pipe insulation R-value
            string? pipeRn = First(text,
                @"At Least R(\d+) Pipe Insulation",
                @"Pipe Insulation[:\s]*R-?(\d+)");
            if (pipeRn != null) info.HotWaterPipeR = $"R{pipeRn}";

            // Mechanical ventilation
            // Primary: match the data row directly — OCR produces a line like:
            //   "Supply Only  145 CFM  9.9  38 Watts  Yes  0"
            // Column headers ("Ventilation Rate [ft³/Min]", "Operational hours per day", "Fan Watts")
            // are separated from the data, so header-based patterns fail on table layouts.
            var ventRow = Regex.Match(text,
                @"(?:Supply Only|Exhaust Only|Balanced|ERV|HRV|Supply|Exhaust)\s+([\d]+)\s*CFM[^\n]{0,80}?([\d]+\.?\d*)[^\n]{0,40}?(\d+)\s*Watts?",
                RegexOptions.IgnoreCase);
            if (ventRow.Success)
            {
                info.TargetFreshAirCfm = ventRow.Groups[1].Value;
                info.TargetRunTime     = ventRow.Groups[2].Value;
                info.VentFanWatts      = ventRow.Groups[3].Value;
            }
            // Fallbacks for inline / non-table formats
            info.TargetFreshAirCfm ??= First(text,
                @"Ventilation Rate[:\s]*([\d.]+)\s*CFM",
                @"Fresh Air[:\s]*([\d.]+)\s*CFM",
                @"Average Mechanical Ventilation[:\s]*([\d.]+)\s*CFM",
                @"(?:Supply Only|Exhaust Only|Balanced|ERV|HRV)\s+([\d]+)\s*CFM");
            info.TargetRunTime ??= First(text, @"Operational hours per day[:\s]*([\d.]+)");
            info.VentFanWatts  ??= First(text, @"Fan Watts[:\s]*(\d+)");

            // Format run time as "9.9 hrs/day (41%)" — the percentage (hrs/24) is applied with the value
            if (info.TargetRunTime != null && double.TryParse(info.TargetRunTime,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double runHrs))
            {
                int pct = (int)Math.Round(runHrs / 24.0 * 100);
                info.TargetRunTime = $"{runHrs} hrs/day ({pct}%)";
            }

            // Energy Star version
            string? esVer = First(text,
                @"ENERGY STAR v([\d.]+)",
                @"Energy Star ([\d.]+)",
                @"ESTAR\s+([\d.]+)");
            if (esVer != null) info.EnergyStarProgram = $"Energy Star {esVer}";

            // IECC code year — "IECC 2015 Label", "IECC 2021 Climate Zone", etc.
            string? ieccYr = First(text,
                @"IECC\s+(\d{4})\s+(?:Label|Climate|Code|Standard)",
                @"IECC\s+(\d{4})");
            if (ieccYr != null) info.IECCVersion = $"IECC {ieccYr}";

            // Water heater: tankless vs. tank
            bool tankless = Regex.IsMatch(text, @"\bTankless\b", RegexOptions.IgnoreCase);
            if (tankless)
            {
                info.WaterHeaterCapacity = "Tankless";
            }
            else
            {
                string? gal = First(text, @"(\d+)\s*gal", @"(\d+)\s*Gallon");
                if (gal != null) info.WaterHeaterCapacity = $"{gal} Gallon";
            }

            // Water heater fuel — check multiple patterns in priority order.
            // 1. Section header: "Tankless WH .82UEF GAS" or "Storage WH 40gal ELECTRIC"
            // 2. Equipment Type line (some report formats)
            // 3. "Natural Gas" / "Electric" / "Propane" row inside the WH section
            // 4. Fuel Type field inside the WH section
            // Do NOT use a bare "Fuel Type" fallback — picks up AC's Electric first.
            string? whFuelRaw = null;
            foreach (var p in new[]
            {
                @"(?:Tankless WH|Storage WH)[^\n]*\b(GAS|PROPANE|ELECTRIC)\b",
                @"Equipment Type:\s*(?:Tankless WH|Water Heat)[^\n]*\b(GAS|PROPANE|ELECTRIC)\b",
                @"(?:Residential Water Heater|Tankless WH|Storage WH)[\s\S]{0,400}?\b(Natural Gas|Propane)\b",
                @"(?:Residential Water Heater|Tankless WH|Storage WH)[\s\S]{0,400}?Fuel Type[:\s]*(Natural Gas|Electric|Propane)",
            })
            {
                var m = Regex.Match(text, p, RegexOptions.IgnoreCase);
                if (m.Success) { whFuelRaw = m.Groups[1].Value; break; }
            }
            if (whFuelRaw != null)
            {
                info.WaterHeaterFuel =
                    whFuelRaw.IndexOf("Propane", StringComparison.OrdinalIgnoreCase) >= 0 ? "Propane" :
                    whFuelRaw.IndexOf("Electric", StringComparison.OrdinalIgnoreCase) >= 0 ? "Electric" :
                    "Gas";
            }
            // (legacy else-branch kept for fallback — Water Heat section + Fuel Type)
            if (info.WaterHeaterFuel == null)
            {
                var whSectionFuel = Regex.Match(text,
                    @"Residential Water Heater[\s\S]{0,300}?Fuel Type[:\s]*(Natural Gas|Electric|Propane)",
                    RegexOptions.IgnoreCase);
                if (!whSectionFuel.Success)
                    whSectionFuel = Regex.Match(text,
                        @"Water Heat(?:er|ing)[\s\S]{0,400}?Fuel Type[:\s]*(Natural Gas|Electric|Propane)",
                        RegexOptions.IgnoreCase);
                if (whSectionFuel.Success)
                {
                    string f = whSectionFuel.Groups[1].Value;
                    info.WaterHeaterFuel =
                        f.IndexOf("Gas", StringComparison.OrdinalIgnoreCase) >= 0 ? "Gas" :
                        f.IndexOf("Electric", StringComparison.OrdinalIgnoreCase) >= 0 ? "Electric" :
                        "Propane";
                }
            }

            // HVAC cooling efficiency — multi-unit: collect all unique SEER values.
            // Label and value may be on the same line or split by a newline in table extraction.
            var seerMatches = Regex.Matches(text,
                @"Cooling Efficiency[\s:]*\r?\n?\s*([\d.]+)\s*(SEER2?)",
                RegexOptions.IgnoreCase);
            var seerValues = seerMatches.Cast<Match>()
                .Select(m => $"{m.Groups[1].Value} {m.Groups[2].Value}")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            // Fallback: bare "15.2 SEER2" pattern found anywhere (e.g., equipment name field)
            if (seerValues.Count == 0)
            {
                seerValues = Regex.Matches(text, @"\b([\d.]+)\s+(SEER2?)\b", RegexOptions.IgnoreCase)
                    .Cast<Match>()
                    .Select(m => $"{m.Groups[1].Value} {m.Groups[2].Value}")
                    .Where(s => { var n = s.Split(' ')[0]; return double.TryParse(n, out double d) && d >= 10 && d <= 30; })
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            if (seerValues.Count > 0)
                info.HvacCoolingSeer = string.Join(" / ", seerValues);

            // HVAC capacity → tonnage → design airflow — multi-unit: sum all cooling capacities.
            // Priority 1: equipment library name "(34K)" — e.g. "15.2 SEER2 A/C (34K)"
            // Priority 2: "Cooling Capacity [kBtu/h]  34" — same line or immediately next line
            var kbtuMatches = Regex.Matches(text, @"\bA/C\s*\((\d+)K\)", RegexOptions.IgnoreCase);
            if (kbtuMatches.Count == 0)
                kbtuMatches = Regex.Matches(text, @"Cooling Capacity[^\r\n\d]*\r?\n?\s{0,10}([\d.]+)", RegexOptions.IgnoreCase);
            double totalKbtu = kbtuMatches.Cast<Match>()
                .Select(m => double.TryParse(m.Groups[1].Value, out double v) ? v : 0)
                .Where(v => v > 0)
                .Sum();
            if (totalKbtu > 0)
            {
                double tons = totalKbtu / 12.0;
                double[] commonSizes = { 1.5, 2.0, 2.5, 3.0, 3.5, 4.0, 4.5, 5.0, 5.5, 6.0, 6.5, 7.0, 7.5, 8.0 };
                double rounded = commonSizes.OrderBy(s => Math.Abs(s - tons)).First();
                info.HvacTonnage      = rounded % 1 == 0 ? ((int)rounded).ToString() : rounded.ToString("0.0");
                info.DesignAirflowCfm = ((int)(rounded * 360)).ToString();
            }
        }

        // ---------------------------------------------------------------
        // Value application helpers
        // ---------------------------------------------------------------

        private static bool SetItemValue(Item item, string value)
        {
            string ctrl = (item.ControlName ?? "").ToLowerInvariant();

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

            // Fallback for unknown controls
            item.Value = value;
            return true;
        }

        private static string? BestLookupMatch(Item item, string target)
        {
            var list = item.ValueList;
            if (list == null || list.Count == 0) return null;

            // 1. Exact match (case-insensitive)
            var exact = list.FirstOrDefault(v =>
                string.Equals(v?.Trim(), target.Trim(), StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;

            // 2. R-number match: extract "R{n}" from target and find first option containing it
            var rMatch = Regex.Match(target, @"R-?\s*(\d+)", RegexOptions.IgnoreCase);
            if (rMatch.Success)
            {
                string rKey = $"R{rMatch.Groups[1].Value}";
                var byR = list.FirstOrDefault(v => v != null && (
                    v.StartsWith(rKey, StringComparison.OrdinalIgnoreCase) ||
                    v.Contains(rKey, StringComparison.OrdinalIgnoreCase)));
                if (byR != null) return byR;
            }

            // 3. SEER match: extract number
            if (target.IndexOf("SEER", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                string seerNum = Regex.Match(target, @"([\d.]+)").Groups[1].Value;
                var bySeer = list.FirstOrDefault(v =>
                    v != null && v.Contains(seerNum, StringComparison.OrdinalIgnoreCase)
                              && v.Contains("SEER", StringComparison.OrdinalIgnoreCase));
                if (bySeer != null) return bySeer;
            }

            // 4. Partial contains
            var contains = list.FirstOrDefault(v =>
                v != null && (
                    v.Contains(target.Trim(), StringComparison.OrdinalIgnoreCase) ||
                    target.Contains(v.Trim(), StringComparison.OrdinalIgnoreCase)));
            if (contains != null) return contains;

            // 5. Keyword shortcuts for common fields
            string lo = target.ToLowerInvariant();
            if (lo == "gas")
            {
                var g = list.FirstOrDefault(v => string.Equals(v?.Trim(), "Gas", StringComparison.OrdinalIgnoreCase));
                if (g != null) return g;
            }
            if (lo == "electric")
            {
                var e = list.FirstOrDefault(v => string.Equals(v?.Trim(), "Electric", StringComparison.OrdinalIgnoreCase));
                if (e != null) return e;
            }
            if (lo.Contains("tankless"))
            {
                var t = list.FirstOrDefault(v => v?.Contains("Tankless", StringComparison.OrdinalIgnoreCase) == true);
                if (t != null) return t;
            }

            return null;
        }

        // ---------------------------------------------------------------
        // Regex helpers
        // ---------------------------------------------------------------

        /// Returns the first group-1 capture from the first matching pattern.
        private static string? First(string text, params string[] patterns)
        {
            foreach (var p in patterns)
            {
                var m = Regex.Match(text, p, RegexOptions.IgnoreCase);
                if (m.Success) return m.Groups[1].Value.Trim();
            }
            return null;
        }

        /// Removes trailing ".0" and trims the number string.
        private static string? CleanNum(string? s)
        {
            if (s == null) return null;
            s = s.Trim();
            if (s.EndsWith(".0")) s = s[..^2];
            return s;
        }

        /// Tries to extract an inch-thickness value (e.g. 7" or 5.5") near a label.
        private static string? InchThickness(string text, string label)
        {
            // Match: label ... digits [.digits] " (inch symbol or ASCII quote)
            var m = Regex.Match(text,
                Regex.Escape(label) + @"[^\n]{0,150}?(\d+\.?\d*)\s*[""″\u2033]",
                RegexOptions.IgnoreCase);
            if (m.Success) return m.Groups[1].Value + "\"";
            return null;
        }

        public static string? GetTessDataPathPublic() => GetTessDataPath();
    }
}
