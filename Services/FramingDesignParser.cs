using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace InspectionEditor.Services
{
    internal sealed class FramingPageText
    {
        public int PageNumber { get; set; }
        public string SheetName { get; set; } = "";
        public string Text { get; set; } = "";
    }

    internal sealed class FramingDesignValue
    {
        public string FieldKey { get; set; } = "";
        public string Value { get; set; } = "";
        public string SourceSheet { get; set; } = "";
        public string Evidence { get; set; } = "";
        public string Confidence { get; set; } = "";
        public bool AppendValue { get; set; } = true;
        public int Priority { get; set; }
    }

    internal sealed class FramingDesignInfo
    {
        public string? PdfPath { get; set; }
        public string? DisplayName { get; set; }
        public string? StatusText { get; set; }
        public string? DebugText { get; set; }
        public bool IsLoaded { get; set; }
        public bool HasFramingPlanEvidence { get; set; }
        public bool HasSecondFloorDesign { get; set; }
        public List<FramingDesignValue> Values { get; set; } = new();

        public IReadOnlyList<FramingDesignValue> GetSuggestionsForPrompt(string? prompt)
        {
            string normalized = FramingDesignParser.NormalizeText(prompt);
            string[] fields;

            if (normalized.Contains("windspeed exposure or c c pressure"))
                fields = new[] { "WindUltimate", "WindContinuous", "ExposureCategory" };
            else if (normalized.Contains("size grade species spacing"))
                fields = new[] { "WallStudSchedule" };
            else if (normalized.Contains("top bottom plate grade species"))
                fields = new[] { "PlateSchedule" };
            else if (normalized.Contains("rafter grade species"))
                fields = new[] { "RafterSchedule", "HipValleyRidgeSchedule" };
            else if (normalized.Contains("ceiling joist grade species"))
                fields = new[] { "CeilingJoistSchedule" };
            else if (normalized.Contains("floor type"))
                fields = new[] { "FloorType", "FloorTypeNotApplicable" };
            else if (normalized.Contains("manufacturer or size grade species"))
                fields = new[] { "FloorProduct", "FloorLumberSchedule", "FloorProductNotApplicable" };
            else if (normalized.Contains("roof sheathing attachment"))
                fields = new[] { "RoofSheathing" };
            else if (normalized.Contains("floor sheathing attachment"))
                fields = new[] { "FloorSheathing" };
            else if (normalized.Contains("exterior non structural sheathing attachment"))
                fields = new[] { "ExteriorNonStructuralSheathing" };
            else if (normalized.Contains("interior non structural sheathing attachment"))
                fields = new[] { "InteriorNonStructuralSheathing" };
            else if (normalized.Contains("structural sheathing attachment"))
                fields = new[] { "StructuralSheathing" };
            else
                return Array.Empty<FramingDesignValue>();

            var fieldSet = fields.ToHashSet(StringComparer.OrdinalIgnoreCase);
            return Values
                .Where(value => fieldSet.Contains(value.FieldKey))
                .OrderByDescending(value => value.Priority)
                .ThenBy(value => value.Value, StringComparer.OrdinalIgnoreCase)
                .GroupBy(value => $"{value.FieldKey}|{FramingDesignParser.NormalizeText(value.Value)}", StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
        }

        public IEnumerable<string> GetSummaryLines()
        {
            return Values
                .Where(value => !string.IsNullOrWhiteSpace(value.Value))
                .OrderBy(value => FramingDesignParser.FieldOrder(value.FieldKey))
                .ThenByDescending(value => value.Priority)
                .GroupBy(value => $"{value.FieldKey}|{FramingDesignParser.NormalizeText(value.Value)}", StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .Select(value => $"{FramingDesignParser.GetFieldLabel(value.FieldKey)}: {value.Value}  [{value.SourceSheet}]");
        }
    }

    internal static class FramingDesignParser
    {
        internal const int ParserVersion = 1;

        internal static FramingDesignInfo Parse(
            string pdfPath,
            IEnumerable<FramingPageText> pageTexts,
            bool extractionComplete)
        {
            var pages = pageTexts
                .Where(page => page != null && !string.IsNullOrWhiteSpace(page.Text))
                .OrderBy(page => page.PageNumber)
                .ToList();

            var info = new FramingDesignInfo
            {
                PdfPath = pdfPath,
                DisplayName = Path.GetFileName(pdfPath),
                IsLoaded = true
            };

            info.HasFramingPlanEvidence = pages.Any(page =>
                Regex.IsMatch(page.Text, @"\b(?:SW|FR)\s*\d+(?:\.\d+)?\b", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(page.Text, @"(?:shear\s+wall|framing)\s+plan", RegexOptions.IgnoreCase) ||
                Regex.IsMatch(page.Text, @"all\s+(?:rafters|ceiling\s+joists|exterior\s+walls|interior\s+walls)\s+shall", RegexOptions.IgnoreCase));

            info.HasSecondFloorDesign = pages.Any(HasFloorSystemPlanEvidence);

            ExtractWind(pages, info);
            ExtractFramingNotes(pages, info);
            ExtractSheathingNotes(pages, info);
            ExtractStructuralSheathing(pages, info);
            ExtractFloorSystem(pages, info, extractionComplete);

            if (info.Values.Any(value => value.FieldKey == "StructuralSheathing" &&
                                         value.Value.StartsWith("OSB —", StringComparison.OrdinalIgnoreCase)))
            {
                info.Values.RemoveAll(value => value.FieldKey == "StructuralSheathing" &&
                                               value.Value.Equals("OSB", StringComparison.OrdinalIgnoreCase));
            }

            info.Values = info.Values
                .OrderBy(value => FieldOrder(value.FieldKey))
                .ThenByDescending(value => value.Priority)
                .GroupBy(value => $"{value.FieldKey}|{NormalizeText(value.Value)}", StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();

            info.StatusText = info.Values.Count > 0
                ? $"{info.Values.Count} framing design value{(info.Values.Count == 1 ? "" : "s")} found."
                : info.HasFramingPlanEvidence
                    ? "Framing plan found; no focused value fields were confidently extracted."
                    : "No applicable framing plan sheets were detected.";

            return info;
        }

        private static void ExtractWind(IEnumerable<FramingPageText> pages, FramingDesignInfo info)
        {
            var candidates = new List<(int Priority, int? Ultimate, int? Continuous, string? Exposure, FramingPageText Page, string Evidence)>();

            foreach (var page in pages)
            {
                string text = CleanText(page.Text);
                int anchor = text.IndexOf("designed based on a windspeed", StringComparison.OrdinalIgnoreCase);
                if (anchor < 0)
                    continue;

                int start = Math.Max(0, anchor - 120);
                int length = Math.Min(text.Length - start, 1100);
                string window = text.Substring(start, length);
                var values = new List<(int Speed, string Label, int Index)>();

                foreach (Match match in Regex.Matches(window,
                    @"(?<!\d)(\d{2,3})\s*mph\s*(?:[^A-Za-z0-9\r\n]{0,8}\(([^)\r\n]{0,18})\))?",
                    RegexOptions.IgnoreCase))
                {
                    if (!int.TryParse(match.Groups[1].Value, out int speed) || speed < 70 || speed > 200)
                        continue;
                    values.Add((speed, match.Groups[2].Value, match.Index));
                }

                var exposureMatch = Regex.Match(window, @"Exposure\s*:\s*([A-D])\b", RegexOptions.IgnoreCase);
                string? exposure = exposureMatch.Success ? exposureMatch.Groups[1].Value.ToUpperInvariant() : null;

                int? ultimate = null;
                int? continuous = null;
                foreach (var value in values)
                {
                    string label = NormalizeText(value.Label);
                    if (label.Contains("ult") || label.StartsWith("vu") || label.StartsWith("vy") || label == "vat")
                        ultimate ??= value.Speed;
                    else if (label.Contains("asd") || label.Contains("asa") || label.Contains("ass") ||
                             label.StartsWith("vas") || label.StartsWith("vgs") || label.StartsWith("ves"))
                        continuous ??= value.Speed;
                }

                if (!ultimate.HasValue && !continuous.HasValue && exposure == null)
                    continue;

                int priority = 2000 - page.PageNumber;
                priority += ultimate.HasValue ? 1000 : 0;
                priority += continuous.HasValue ? 1000 : 0;
                priority += exposure != null ? 500 : 0;
                if (string.Equals(page.SheetName, "SW1", StringComparison.OrdinalIgnoreCase))
                    priority += 500;
                else if (page.SheetName.StartsWith("SW", StringComparison.OrdinalIgnoreCase))
                    priority += 350;
                else if (page.SheetName.StartsWith("FR", StringComparison.OrdinalIgnoreCase))
                    priority += 200;
                else if (page.SheetName.StartsWith("FJ", StringComparison.OrdinalIgnoreCase))
                    priority -= 200;

                candidates.Add((priority, ultimate, continuous, exposure, page, CleanEvidence(window, 260)));
            }

            var ultimateCandidate = candidates
                .Where(candidate => candidate.Ultimate.HasValue)
                .OrderByDescending(candidate => candidate.Priority)
                .FirstOrDefault();
            if (ultimateCandidate.Page != null && ultimateCandidate.Ultimate.HasValue)
                AddPreferred(info, "WindUltimate", $"{ultimateCandidate.Ultimate.Value} mph Vult",
                    ultimateCandidate.Page, ultimateCandidate.Evidence, "High", ultimateCandidate.Priority);

            var continuousCandidate = candidates
                .Where(candidate => candidate.Continuous.HasValue)
                .OrderByDescending(candidate => candidate.Priority)
                .FirstOrDefault();
            if (continuousCandidate.Page != null && continuousCandidate.Continuous.HasValue)
                AddPreferred(info, "WindContinuous", $"{continuousCandidate.Continuous.Value} mph Vasd",
                    continuousCandidate.Page, continuousCandidate.Evidence, "High", continuousCandidate.Priority);

            var exposureCandidate = candidates
                .Where(candidate => !string.IsNullOrWhiteSpace(candidate.Exposure))
                .OrderByDescending(candidate => candidate.Priority)
                .FirstOrDefault();
            if (exposureCandidate.Page != null && !string.IsNullOrWhiteSpace(exposureCandidate.Exposure))
                AddPreferred(info, "ExposureCategory", $"Exposure {exposureCandidate.Exposure}",
                    exposureCandidate.Page, exposureCandidate.Evidence, "High", exposureCandidate.Priority);
        }

        private static void ExtractFramingNotes(IEnumerable<FramingPageText> pages, FramingDesignInfo info)
        {
            foreach (var page in pages)
            {
                string text = CleanText(page.Text);
                int priority = 1500 - page.PageNumber;
                if (page.SheetName.StartsWith("FR", StringComparison.OrdinalIgnoreCase))
                    priority += 250;

                AddRule(info, page, text, "CeilingJoistSchedule",
                    @"All\s+ceiling\s+joists\s+shall\s+be\s+(?<value>.{3,150}?(?:UNO|unless\s+noted\s+otherwise)\.?)",
                    "Ceiling joists", priority);

                AddRule(info, page, text, "RafterSchedule",
                    @"All\s+rafters\s+shall\s+be\s+(?<value>.{3,180}?(?:UNO|unless\s+noted\s+otherwise)\.?)",
                    "Rafters", priority);

                AddRule(info, page, text, "HipValleyRidgeSchedule",
                    @"Hips,?\s*valleys,?\s*(?:and|&)\s*ridges\s+shall\s+be\s+(?<value>.{3,160}?(?:UNO|unless\s+noted\s+otherwise)\.?)",
                    "Hips/Valleys/Ridges", priority);

                AddRule(info, page, text, "WallStudSchedule",
                    @"All\s+exterior\s+walls\s+shall\s+be\s+(?<value>.{3,180}?(?:UNO|unless\s+noted\s+otherwise)\.?)",
                    "Exterior", priority);

                AddRule(info, page, text, "WallStudSchedule",
                    @"All\s+interior\s+walls(?:\s+including\s+under\s+the\s+floor\s+system)?\s+shall\s+be\s+(?<value>.{3,240}?(?:UNO|unless\s+noted\s+otherwise)\.?)",
                    "Interior", priority);

                foreach (Match match in Regex.Matches(text,
                    @"(?:top|bottom|sill|base)\s+plates?[^.\r\n]{0,100}?\b(SPF|SYP|D\.?\s*Fir|Douglas\s+Fir)\b[^.\r\n]{0,50}?(?:No\.?|#)\s*([123])\b",
                    RegexOptions.IgnoreCase))
                {
                    string species = NormalizeSpecies(match.Groups[1].Value);
                    AddPreferred(info, "PlateSchedule", $"{species} #{match.Groups[2].Value}", page,
                        CleanEvidence(match.Value, 180), "Medium", priority);
                }

                var treated = Regex.Match(text,
                    @"All\s+sills\s+on\s+concrete\s+slabs\s+shall\s+be\s+treated\s+lumber",
                    RegexOptions.IgnoreCase);
                if (treated.Success)
                    AddPreferred(info, "PlateSchedule", "Treated Base", page, treated.Value, "High", priority);
            }
        }

        private static void ExtractSheathingNotes(IEnumerable<FramingPageText> pages, FramingDesignInfo info)
        {
            foreach (var page in pages)
            {
                string text = CleanText(page.Text);
                int priority = 1200 - page.PageNumber;

                var roof = Regex.Match(text,
                    @"Roof\s+sheathing\s+shall\s+be\s+minimum\s+(?<thickness>\d+\s*/\s*\d+)\s*(?:inches?|[""”])?\s*thickness\s+sheathing\s+with\s+(?<rating>\d+\s*/\s*\d+)\s*span",
                    RegexOptions.IgnoreCase);
                if (roof.Success)
                {
                    string value = $"{CompactFraction(roof.Groups["thickness"].Value)}\" {CompactFraction(roof.Groups["rating"].Value)} span rating";
                    AddPreferred(info, "RoofSheathing", value, page, CleanEvidence(roof.Value, 220), "High", priority);
                }

                var floor = Regex.Match(text,
                    @"Floor\s+sheathing\s+shall\s+be\s+minimum\s+(?<thickness>\d+\s*/\s*\d+)\s*(?:inches?|[""”])?\s*thickness\s+T\s*&?\s*G\s+sheathing[\s\S]{0,180}?with\s+(?<rating>\d+\s*/\s*\d+)\s*span",
                    RegexOptions.IgnoreCase);
                if (floor.Success)
                {
                    string value = $"{CompactFraction(floor.Groups["thickness"].Value)}\" T&G {CompactFraction(floor.Groups["rating"].Value)} span rating";
                    AddPreferred(info, "FloorSheathing", value, page, CleanEvidence(floor.Value, 260), "High", priority);
                }
            }
        }

        private static void ExtractStructuralSheathing(IEnumerable<FramingPageText> pages, FramingDesignInfo info)
        {
            foreach (var page in pages)
            {
                string text = CleanText(page.Text);
                int priority = 1400 - page.PageNumber;
                if (page.SheetName.StartsWith("SW", StringComparison.OrdinalIgnoreCase))
                    priority += 300;

                foreach (Match match in Regex.Matches(text,
                    @"Engineered\s+Shear\s+Wall\s*[-–]?\s*(?<material>Green\s+T\s*-?\s*Ply|Red\s+T\s*-?\s*Ply|OSB)(?<tail>.{0,100})",
                    RegexOptions.IgnoreCase))
                {
                    string material = Regex.Replace(match.Groups["material"].Value, @"\s+", " ").Replace("T -", "T-").Trim();
                    string tail = match.Groups["tail"].Value;
                    string value;
                    if (material.Contains("T-Ply", StringComparison.OrdinalIgnoreCase) || material.Contains("T Ply", StringComparison.OrdinalIgnoreCase))
                    {
                        material = material.Replace("T Ply", "T-Ply", StringComparison.OrdinalIgnoreCase);
                        value = $"{material} — 3\" edge / 6\" middle";
                    }
                    else
                    {
                        var edge = Regex.Match(tail, @"(?<spacing>\d+)\s*(?:in\.?|inch|[""”])\s*edge\s+nailing", RegexOptions.IgnoreCase);
                        value = edge.Success
                            ? $"OSB — {edge.Groups["spacing"].Value}\" edge nailing"
                            : "OSB";
                    }

                    AddPreferred(info, "StructuralSheathing", value, page, CleanEvidence(match.Value, 220), "High", priority);
                }

                foreach (Match nonStructuralMatch in Regex.Matches(text,
                    @"[^.\r\n]{0,120}(?:non\s*[- ]?structural[^.\r\n]{0,180}?T\s*-?\s*Ply|T\s*-?\s*Ply[^.\r\n]{0,180}?non\s*[- ]?structural)[^.\r\n]{0,220}",
                    RegexOptions.IgnoreCase))
                {
                    string usageWindow = nonStructuralMatch.Value;
                    bool explicitlyNonThermal = Regex.IsMatch(usageWindow,
                        @"non\s*[- ]?thermal\s+boundary|outside\s+(?:the\s+)?thermal\s+boundary",
                        RegexOptions.IgnoreCase);
                    bool airBarrier = !explicitlyNonThermal &&
                        Regex.IsMatch(usageWindow, @"air\s+barrier|thermal\s+boundary", RegexOptions.IgnoreCase);
                    bool exterior = Regex.IsMatch(usageWindow, @"exterior", RegexOptions.IgnoreCase);
                    bool interior = Regex.IsMatch(usageWindow, @"interior", RegexOptions.IgnoreCase);

                    if (airBarrier && exterior)
                        AddPreferred(info, "ExteriorNonStructuralSheathing", "T-Ply — 6\" edge / 6\" middle", page,
                            CleanEvidence(usageWindow, 260), "Medium", priority);
                    else if (interior && !airBarrier)
                        AddPreferred(info, "InteriorNonStructuralSheathing", "T-Ply — 6\" edge / 12\" middle", page,
                            CleanEvidence(usageWindow, 260), "Medium", priority);
                }
            }
        }

        private static bool HasFloorSystemPlanEvidence(FramingPageText page)
        {
            string sheet = Regex.Replace(page.SheetName ?? "", @"\s+", "");
            if (Regex.IsMatch(sheet, @"^FJ(?!0(?:\.|$))\d+(?:\.\d+)?$", RegexOptions.IgnoreCase))
                return true;

            return Regex.IsMatch(page.Text, @"\bFJ[1-9]\d*(?:\.\d+)?\b", RegexOptions.IgnoreCase) ||
                   Regex.IsMatch(page.Text,
                       @"(?:(?:second|2nd|upper)\s+(?:floor|level).{0,80}?(?:shear\s+wall|framing|joists?|layout)|floor\s+joist\s+layout|floor\s+framing\s+plan)",
                       RegexOptions.IgnoreCase);
        }

        private static void ExtractFloorSystem(IEnumerable<FramingPageText> pages, FramingDesignInfo info, bool extractionComplete)
        {
            const string openWebPattern = @"open\s*[- ]?web\s+(?:floor\s+)?(?:joists?|trusses?)";
            var floorPages = pages.Where(page =>
                HasFloorSystemPlanEvidence(page) ||
                Regex.IsMatch(page.Text, $@"I\s*[- ]?Joists?\s+per\s+plan|{openWebPattern}", RegexOptions.IgnoreCase))
                .ToList();

            string floorText = CleanText(string.Join("\n", floorPages.Select(page => page.Text)));
            FramingPageText? source = floorPages.FirstOrDefault();
            int priority = source != null ? 1300 - source.PageNumber : 700;

            if (Regex.IsMatch(floorText, openWebPattern, RegexOptions.IgnoreCase))
            {
                AddPreferred(info, "FloorType", "Open Web", source, "Open-web floor-system callout.", "High", priority);
                var selectedOpenWebProduct = Regex.Match(floorText,
                    $@"{openWebPattern}[^.\r\n]{{0,140}}?(?:by|manufacturer|product|series|model|size|grade|species)\s*[:=]?\s*(?<product>[A-Za-z0-9][A-Za-z0-9 .#/-]{{1,60}})",
                    RegexOptions.IgnoreCase);
                if (selectedOpenWebProduct.Success)
                    AddPreferred(info, "FloorProduct", CleanEvidence(selectedOpenWebProduct.Groups["product"].Value, 80), source,
                        CleanEvidence(selectedOpenWebProduct.Value, 180), "Medium", priority);
                else
                    AddPreferred(info, "FloorProductNotApplicable", "NI", source,
                        "Open-web manufacturer/product/species/grade was not stated in the selected floor-system callout.",
                        "High", priority, appendValue: false);
                return;
            }

            if (Regex.IsMatch(floorText, @"I\s*[- ]?Joists?\s+per\s+plan", RegexOptions.IgnoreCase))
            {
                AddPreferred(info, "FloorType", "I-Joist", source, "I-Joist per plan.", "High", priority);

                var selected = Regex.Match(floorText,
                    @"(?:floor\s+system|floor\s+joists?)\s*(?:shall\s+be|:|=)\s*(?<product>(?:TJI|BCI|LPI)\s*[A-Z0-9.-]+)",
                    RegexOptions.IgnoreCase);
                if (selected.Success)
                    AddPreferred(info, "FloorProduct", CleanEvidence(selected.Groups["product"].Value, 80), source,
                        CleanEvidence(selected.Value, 160), "Medium", priority);
                else
                    AddPreferred(info, "FloorProductNotApplicable", "NI", source, "I-Joist species/grade is not applicable.", "High", priority, appendValue: false);
                return;
            }

            var dimensional = Regex.Match(floorText,
                @"(?:floor\s+joists?|floor\s+system)\s+shall\s+be\s+(?<value>2x\d+.{0,100}?(?:UNO|\.))",
                RegexOptions.IgnoreCase);
            if (dimensional.Success)
            {
                AddPreferred(info, "FloorType", "Dimensional Lumber", source, CleanEvidence(dimensional.Value, 180), "High", priority);
                AddPreferred(info, "FloorLumberSchedule", CleanEvidence(dimensional.Groups["value"].Value, 140), source,
                    CleanEvidence(dimensional.Value, 180), "High", priority);
                return;
            }

            if (extractionComplete && info.HasFramingPlanEvidence && info.Values.Count > 0 && !info.HasSecondFloorDesign)
            {
                AddPreferred(info, "FloorTypeNotApplicable", "NI", pages.FirstOrDefault(),
                    "No second-floor shear-wall or floor-system plan was detected.", "Medium", priority, appendValue: false);
                AddPreferred(info, "FloorProductNotApplicable", "NI", pages.FirstOrDefault(),
                    "No second-floor shear-wall or floor-system plan was detected.", "Medium", priority, appendValue: false);
            }
        }

        private static void AddRule(
            FramingDesignInfo info,
            FramingPageText page,
            string text,
            string field,
            string pattern,
            string label,
            int priority)
        {
            var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
            if (!match.Success)
                return;

            string value = CleanEvidence(match.Groups["value"].Value, 180);
            value = Regex.Replace(value, @"\s+(?:UNO|unless\s+noted\s+otherwise)\.?$", "", RegexOptions.IgnoreCase).Trim(' ', '.');
            if (value.Length < 3)
                return;

            AddPreferred(info, field, $"{label}: {value}", page, CleanEvidence(match.Value, 240), "High", priority);
        }

        private static void AddPreferred(
            FramingDesignInfo info,
            string field,
            string value,
            FramingPageText? page,
            string evidence,
            string confidence,
            int priority,
            bool appendValue = true)
        {
            value = CleanEvidence(value, 220);
            if (string.IsNullOrWhiteSpace(value))
                return;

            string normalized = NormalizeText(value);
            var existing = info.Values.FirstOrDefault(candidate =>
                string.Equals(candidate.FieldKey, field, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(NormalizeText(candidate.Value), normalized, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                if (priority > existing.Priority)
                {
                    existing.Priority = priority;
                    existing.SourceSheet = SourceLabel(page);
                    existing.Evidence = CleanEvidence(evidence, 320);
                    existing.Confidence = confidence;
                    existing.AppendValue = appendValue;
                }
                return;
            }

            info.Values.Add(new FramingDesignValue
            {
                FieldKey = field,
                Value = value,
                SourceSheet = SourceLabel(page),
                Evidence = CleanEvidence(evidence, 320),
                Confidence = confidence,
                AppendValue = appendValue,
                Priority = priority
            });
        }

        private static string SourceLabel(FramingPageText? page)
        {
            if (page == null)
                return "Plan set";
            return !string.IsNullOrWhiteSpace(page.SheetName)
                ? page.SheetName
                : $"PDF page {page.PageNumber}";
        }

        internal static string DetectSheetName(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";

            var labels = Regex.Matches(text, @"\b(?:SW|FR|FJ)\s*\d+(?:\.\d+)?\b", RegexOptions.IgnoreCase)
                .Cast<Match>()
                .Select(match => Regex.Replace(match.Value.ToUpperInvariant(), @"\s+", ""))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return labels.Count == 1 ? labels[0] : "";
        }

        internal static bool IsCandidateSheetText(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            return Regex.IsMatch(text, @"\b(?:SW|FR|FJ)\s*\d+(?:\.\d+)?\b", RegexOptions.IgnoreCase) ||
                   Regex.IsMatch(text, @"designed\s+based\s+on\s+a\s+windspeed|all\s+(?:rafters|ceiling\s+joists|exterior\s+walls|interior\s+walls)\s+shall|(?:roof|floor)\s+sheathing\s+shall|engineered\s+shear\s+wall", RegexOptions.IgnoreCase);
        }

        internal static bool ValueAlreadyContains(string? currentValue, string? suggestedValue)
        {
            string suggested = NormalizeText(suggestedValue);
            if (string.IsNullOrWhiteSpace(currentValue) || string.IsNullOrWhiteSpace(suggested))
                return false;

            return Regex.Split(currentValue, @"[|;\r\n]+")
                .Select(NormalizeText)
                .Any(token => string.Equals(token, suggested, StringComparison.OrdinalIgnoreCase));
        }

        internal static string AppendValue(string? currentValue, string suggestedValue)
        {
            if (string.IsNullOrWhiteSpace(currentValue))
                return suggestedValue.Trim();
            if (ValueAlreadyContains(currentValue, suggestedValue))
                return currentValue.Trim();
            return $"{currentValue.Trim()} | {suggestedValue.Trim()}";
        }

        internal static string NormalizeText(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "";

            string text = value
                .Replace('&', ' ')
                .Replace('/', ' ')
                .Replace('–', ' ')
                .Replace('—', ' ')
                .Replace('-', ' ')
                .Replace('"', ' ')
                .Replace('”', ' ')
                .Replace('“', ' ')
                .ToLowerInvariant();
            return Regex.Replace(text, @"[^a-z0-9]+", " ").Trim();
        }

        internal static int FieldOrder(string field) => field switch
        {
            "WindUltimate" => 10,
            "WindContinuous" => 11,
            "ExposureCategory" => 12,
            "WallStudSchedule" => 20,
            "PlateSchedule" => 30,
            "RafterSchedule" => 40,
            "HipValleyRidgeSchedule" => 41,
            "CeilingJoistSchedule" => 50,
            "FloorType" => 60,
            "FloorProduct" => 61,
            "FloorLumberSchedule" => 62,
            "FloorTypeNotApplicable" => 63,
            "FloorProductNotApplicable" => 64,
            "RoofSheathing" => 70,
            "FloorSheathing" => 80,
            "StructuralSheathing" => 90,
            "ExteriorNonStructuralSheathing" => 100,
            "InteriorNonStructuralSheathing" => 110,
            _ => 999
        };

        internal static string GetFieldLabel(string field) => field switch
        {
            "WindUltimate" => "Ultimate wind",
            "WindContinuous" => "Continuous wind",
            "ExposureCategory" => "Exposure",
            "WallStudSchedule" => "Wall studs",
            "PlateSchedule" => "Plates/sills",
            "RafterSchedule" => "Rafters",
            "HipValleyRidgeSchedule" => "Hips/valleys/ridges",
            "CeilingJoistSchedule" => "Ceiling joists",
            "FloorType" => "Floor type",
            "FloorProduct" => "Floor product",
            "FloorLumberSchedule" => "Floor lumber",
            "FloorTypeNotApplicable" => "Floor type",
            "FloorProductNotApplicable" => "Floor product/species",
            "RoofSheathing" => "Roof sheathing",
            "FloorSheathing" => "Floor sheathing",
            "StructuralSheathing" => "Structural sheathing",
            "ExteriorNonStructuralSheathing" => "Exterior non-structural sheathing",
            "InteriorNonStructuralSheathing" => "Interior non-structural sheathing",
            _ => field
        };

        private static string CleanText(string? text)
        {
            return (text ?? "")
                .Replace('\u201c', '"')
                .Replace('\u201d', '"')
                .Replace('\u2019', '\'')
                .Replace('\u00a0', ' ');
        }

        private static string CleanEvidence(string? text, int maxLength)
        {
            string cleaned = Regex.Replace(CleanText(text), @"\s+", " ").Trim();
            return cleaned.Length <= maxLength ? cleaned : cleaned.Substring(0, maxLength).TrimEnd() + "…";
        }

        private static string CompactFraction(string value) => Regex.Replace(value, @"\s+", "");

        private static string NormalizeSpecies(string value)
        {
            string normalized = Regex.Replace(value, @"\s+", " ").Trim();
            if (Regex.IsMatch(normalized, @"^D\.?\s*Fir$", RegexOptions.IgnoreCase) ||
                normalized.Equals("Douglas Fir", StringComparison.OrdinalIgnoreCase))
                return "D.Fir";
            return normalized.ToUpperInvariant();
        }
    }
}
