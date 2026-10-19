using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace InspectionEditor.Services.Reports
{
    public enum ReportKind { Default, SpecialConsult, Nho }
    public enum NhoVariant { Plain, Form97, Form129 }
    public enum SpecialBand { None, Slab, PrePour, Truck }

    public sealed class ReportPicture
    {
        public required byte[] Bytes { get; init; }
    }

    public sealed class ReportItem
    {
        public string Number { get; set; } = "";
        public string Name { get; set; } = "";
        public string Value { get; set; } = "";
        public string Comments { get; set; } = "";
        public int Incidents { get; set; }
        public bool AlwaysPrint { get; set; }
        public string OnlyPrint { get; set; } = "";
        public List<ReportPicture> Pictures { get; set; } = new();
        // NHO (L26)
        public string Line1 { get; set; } = "";
        public string Line2 { get; set; } = "";
        public string Indicator { get; set; } = "";   // "", "check-green", "cross-red", "cross-grey"
        public bool ActionItem { get; set; }
        public bool FirstOfGroup { get; set; } = true;

        // P3: the value cell text: (value part, coloured Fail?, the comment part).
        public (string ValuePart, bool Fail, string Tail) CombinedValue()
        {
            string value = Value;
            if (Incidents > 0) value += $" ({Incidents.ToString(CultureInfo.InvariantCulture)})";
            bool fail = Value == "Fail";
            if (string.IsNullOrWhiteSpace(Comments)) return (value, fail, "");
            if (string.IsNullOrWhiteSpace(Value) || Value == "ok") return ("", false, Comments);
            return (value, fail, " - " + Comments);
        }
    }

    public sealed class ReportSection
    {
        public string Number { get; set; } = "";
        public string Name { get; set; } = "";
        public List<ReportItem> Items { get; set; } = new();
        public SpecialBand Band { get; set; }
        public bool BandPrints { get; set; }     // P5: the special table prints only on its count condition
        public bool PrePourPictures { get; set; } // LB2 picture grid (exactly 24)
        public bool IsSign { get; set; }          // NHO Sign section
        public bool IsActionSection { get; set; }
    }

    public sealed class ReportDocument
    {
        public ReportKind Kind { get; set; }
        public NhoVariant Variant { get; set; }
        public bool SecondWalk { get; set; }
        public int FormId { get; set; }
        public bool Kyle { get; set; }
        public Dictionary<string, string> Header { get; } = new(StringComparer.Ordinal);
        public int StatusId { get; set; }
        public byte[]? Signature { get; set; }
        public byte[]? CustomerSignature { get; set; }
        public byte[]? PlacementDiagram { get; set; }
        public byte[]? SketchDiagram { get; set; }      // Special Consult section 2
        public bool IsPreinspection { get; set; }
        public string? BuilderAccountId { get; set; }
        public DateTime? DateInspected { get; set; }
        public List<ReportSection> Sections { get; } = new();
        public JArray Attachments { get; set; } = new();
        public string CustomerName { get; set; } = "";
        public string CustomerPhone { get; set; } = "";
        public string H(string key) => Header.TryGetValue(key, out var v) ? v : "";
    }

    /// <summary>F-022 §4-§7: the report data INSPECT's report classes build, from the INSPECT-dialect document.</summary>
    public static class ReportBuilder
    {
        private static readonly CultureInfo EnUs = CultureInfo.GetCultureInfo("en-US");
        private static readonly int[] NhoPlain = { 67, 70, 71, 76, 77, 78, 79, 80, 84, 119, 160 };

        // RL L52 (F-022 research, exact INSPECT lists; compared ignoring case).
        public static readonly string[] ExcludeFromItemComments =
        {
            "Microwave Serial Number", "Stove/Oven Serial Number", "Dishwasher Serial Number", "Refrigerator Serial Number",
            "Microwave Model Number", "Stove/Oven Model Number", "Dishwasher Model Number", "Refrigerator Model Number",
            "Electric Provider", "Gas Provider", "Water Provider", "New home sign-off date", "New home sign-off time"
        };
        public static readonly string[] ExcludeFromActionItems =
        {
            "Have all items been addressed?", "Customer Signature Documents", "Customer Information Documents", "Home orientation form",
            "Other Items", "New home sign-off date", "New home sign-off time", "Customer signature"
        };

        public static string StatusName(int id) => id switch
        {
            0 => "Ordered", 1 => "Scheduled", 2 => "Passed", 3 => "Failed", 4 => "Cancelled", 5 => "Correct & Proceed",
            7 => "Complete", 9 => "Not Ready", _ => "N/A"
        };

        public static string StructureName(int id) => id switch
        {
            0 => "Main House", 1 => "Pavilion", 2 => "Pool House", 3 => "Outdoor Kitchen", 4 => "Addition", _ => "N/A"
        };

        // §4 dispatch.
        public static (ReportKind Kind, NhoVariant Variant, bool Kyle) Dispatch(int formId, string? city, int statusId)
        {
            bool kyle = formId == 42 && string.Equals(city, "Kyle", StringComparison.Ordinal) && statusId is 2 or 5;
            if (formId == 44) return (ReportKind.SpecialConsult, NhoVariant.Plain, kyle);
            if (formId == 97) return (ReportKind.Nho, NhoVariant.Form97, kyle);
            if (formId == 129) return (ReportKind.Nho, NhoVariant.Form129, kyle);
            if (Array.IndexOf(NhoPlain, formId) >= 0) return (ReportKind.Nho, NhoVariant.Plain, kyle);
            return (ReportKind.Default, NhoVariant.Plain, kyle);
        }

        public static ReportDocument Build(JObject doc, ReportOptions options)
        {
            int formId = I(doc["FormId"]);
            int statusId = I(doc["StatusId"]);
            var (kind, variant, kyle) = Dispatch(formId, S(doc["City"]), statusId);
            var report = new ReportDocument
            {
                Kind = kind, Variant = variant, Kyle = kyle, FormId = formId, StatusId = statusId,
                SecondWalk = B(doc["IsReinspection"]),
                Signature = Image(doc["Signature"]),
                IsPreinspection = B(doc["IsPreinspection"]),
                BuilderAccountId = NullIfBlank(S(doc["BuilderAccountId"])),
                DateInspected = Date(doc["DateInspected"]),
                Attachments = doc["Attachments"] as JArray ?? new JArray()
            };
            FillHeader(report, doc);

            var sections = (doc["Sections"] as JArray)?.OfType<JObject>().Select(ToSection).ToList() ?? new List<ReportSection>();
            if (kind == ReportKind.Nho)
                sections = sections.OrderBy(s => NumberKey(s.Number)).ToList(); // N1: sorted by Number
            ApplyOptions(sections, options);                                    // P1/P2 (NHO: RemovePictures, ScrubItemList)

            switch (kind)
            {
                case ReportKind.SpecialConsult:
                    ScrubSpecialConsult(sections);
                    var second = sections.FirstOrDefault(s => s.Number == "2");
                    if (second != null)
                    {
                        report.SketchDiagram = second.Items.FirstOrDefault()?.Pictures.FirstOrDefault()?.Bytes; // S2: Items[0].Pictures[0], drawn once
                        foreach (var item in second.Items) item.Pictures.Clear();                               // no ordinary pictures in section 2
                    }
                    break;
                case ReportKind.Default:
                    ApplySpecialBands(report, sections);
                    break;
                case ReportKind.Nho:
                    BuildNho(report, sections);
                    break;
            }
            report.Sections.AddRange(sections);
            return report;
        }

        private static void FillHeader(ReportDocument report, JObject doc)
        {
            string address = S(doc["Address"]), city = S(doc["City"]);
            report.Header["InspectionName"] = S(doc["InspectionName"]);
            report.Header["InspectionNumber"] = S(doc["InspectionNumber"]);
            report.Header["AddressCombo"] = string.Join(", ", new[] { address, city }.Where(s => s.Length > 0));
            report.Header["Address"] = address;
            report.Header["City"] = city;
            report.Header["Project"] = S(doc["Project"]);
            string lot = S(doc["Lot"]), block = S(doc["Block"]);
            report.Header["LotBlock"] = lot.Length > 0 && block.Length > 0 ? $"{lot}/{block}" : lot + block;
            report.Header["PlanNumber"] = S(doc["PlanNumber"]);
            report.Header["InspectorName"] = S(doc["InspectorName"]);
            report.Header["InspectorPhone"] = S(doc["InspectorPhone"]);
            report.Header["BuilderName"] = S(doc["BuilderName"]);
            report.Header["JobNumber"] = S(doc["JobNumber"]);
            report.Header["Contact"] = S(doc["Contact"]);
            report.Header["ContactNumber"] = S(doc["ContactNumber"]);
            report.Header["Complete"] = report.DateInspected?.ToString("MMM d, yyyy h:mm tt", EnUs) ?? "";
            report.Header["PO"] = S(doc["PurchaseOrderNumber"]);
            report.Header["Structure"] = StructureName(I(doc["StructureTypeId"]));
            report.Header["Super"] = S(doc["SuperintendentName"]);
            report.Header["Status"] = StatusName(report.StatusId);
            report.Header["NextAction"] = string.IsNullOrWhiteSpace(S(doc["NextActionText"])) ? "" : "Next action: " + S(doc["NextActionText"]);
            report.Header["Notified"] = Notified(S(doc["WhoNotified"]), Date(doc["WhenNotified"]));
        }

        // LA5 NotifyString.
        public static string Notified(string who, DateTime? when)
        {
            if (string.IsNullOrWhiteSpace(who)) return "";
            string name = EnUs.TextInfo.ToTitleCase(who.Trim().ToLower(EnUs));
            return when.HasValue ? $"{name} on {when.Value.ToString("MM/dd/yyyy hh:mm", EnUs)}" : name;
        }

        private static ReportSection ToSection(JObject section) => new()
        {
            Number = S(section["Number"]),
            Name = S(section["Name"]),
            Items = (section["Items"] as JArray)?.OfType<JObject>().Select(ToItem).ToList() ?? new List<ReportItem>()
        };

        private static ReportItem ToItem(JObject item) => new()
        {
            Number = S(item["Number"]),
            Name = S(item["Name"]),
            Value = S(item["Value"]),
            Comments = S(item["Comments"]),
            Incidents = I(item["Incidents"]),
            AlwaysPrint = B(item["AlwaysPrint"]),
            OnlyPrint = S(item["OnlyPrint"]),
            Pictures = (item["Pictures"] as JArray)?.OfType<JObject>()
                .Select(p => Image(p["Image"] ?? p["Data"])).Where(b => b != null).Select(b => new ReportPicture { Bytes = b! }).ToList() ?? new List<ReportPicture>()
        };

        // P1/P2.
        public static void ApplyOptions(List<ReportSection> sections, ReportOptions options)
        {
            if (options.DefectsOnly)
            {
                foreach (var section in sections)
                {
                    section.Items = section.Items.Where(i => i.AlwaysPrint || i.Value == "Fail" || i.OnlyPrint.Length > 0 ||
                        i.Name is "Comments" or "Other Comments").ToList();
                    section.Items.RemoveAll(i => i.OnlyPrint.Length > 0 && i.Value != i.OnlyPrint);
                }
                if (!options.Pictures) foreach (var item in sections.SelectMany(s => s.Items)) item.Pictures.Clear();
                sections.RemoveAll(s => s.Items.Count == 0);
            }
            else if (!options.Pictures)
            {
                foreach (var item in sections.SelectMany(s => s.Items)) item.Pictures.Clear();
            }
        }

        // S1 (FormId 44 only).
        public static void ScrubSpecialConsult(List<ReportSection> sections)
        {
            foreach (var section in sections)
            {
                foreach (var item in section.Items.Where(i => i.Value == "ok")) item.Value = "";
                section.Items.RemoveAll(i => (i.Name.StartsWith("Comment", StringComparison.Ordinal) || i.Name.StartsWith("Additional", StringComparison.Ordinal)) &&
                                             string.IsNullOrWhiteSpace(i.Comments) && i.Pictures.Count == 0);
            }
            sections.RemoveAll(s => s.Items.Count == 0);
        }

        // P5 special bands (Default report only).
        public static void ApplySpecialBands(ReportDocument report, List<ReportSection> sections)
        {
            foreach (var section in sections)
            {
                if (report.FormId == 20 && section.Number == "8") { section.Band = SpecialBand.Slab; section.BandPrints = section.Items.Count == 24; }
                else if (report.FormId == 68 && section.Number == "8") { section.Band = SpecialBand.PrePour; section.BandPrints = section.Items.Count >= 24; section.PrePourPictures = section.Items.Count == 24; }
                else if (report.FormId == 27 && section.Number == "4") { section.Band = SpecialBand.Truck; section.BandPrints = section.Items.Count == 60; }
            }
            var truck = sections.FirstOrDefault(s => s.Band == SpecialBand.Truck && s.BandPrints);
            if (truck != null)
            {
                foreach (var section in sections)
                {
                    var diagram = section.Items.FirstOrDefault(i => i.Name == "Placement Diagram");
                    if (diagram == null) continue;
                    report.PlacementDiagram = diagram.Pictures.FirstOrDefault()?.Bytes;
                    section.Items.Remove(diagram); // drawn in the truck band instead
                    break;
                }
            }
        }

        // §6 New Home Orientation (L24, L26, L34).
        private static void BuildNho(ReportDocument report, List<ReportSection> sections)
        {
            // ActionItemSections: [2] by default; FormId 97/129 add every integer 2..max. A section is listed when its Number is
            // exactly one of those integer strings.
            bool IsActionSection(string number)
            {
                if (report.Variant == NhoVariant.Plain) return number == "2";
                double key = NumberKey(number);
                return key >= 2 && key < int.MaxValue && Math.Floor(key) == key &&
                       number == ((int)key).ToString(CultureInfo.InvariantCulture);
            }
            int actionNumber = 0;
            foreach (var section in sections)
            {
                section.IsSign = section.Name == "Sign";
                section.IsActionSection = IsActionSection(section.Number);
                string? previousNumber = null;
                foreach (var item in section.Items)
                {
                    item.FirstOfGroup = item.Number != previousNumber;
                    previousNumber = item.Number;
                    if (!section.IsActionSection)
                    {
                        item.Line1 = $"{item.Name}: {item.Value}";
                        continue;
                    }
                    bool excluded = ExcludeFromItemComments.Contains(item.Name, StringComparer.InvariantCultureIgnoreCase);
                    var (indicator, comments) = Indicator(report.Variant, item.Value);
                    item.Line1 = excluded ? $"{item.Name}: {item.Value}" : item.FirstOfGroup ? item.Name : "";
                    item.Indicator = excluded || !item.FirstOfGroup ? "" : indicator;
                    bool hasComment = !string.IsNullOrWhiteSpace(item.Comments);
                    if (!excluded && (hasComment || item.Pictures.Count > 0))
                    {
                        actionNumber++;
                        string text = hasComment ? char.ToUpper(item.Comments.TrimStart()[0], EnUs) + item.Comments.TrimStart().Substring(1) : "See Picture";
                        item.Line2 = $"Item {actionNumber.ToString(CultureInfo.InvariantCulture)}:  {text}";
                        item.ActionItem = comments && !ExcludeFromActionItems.Contains(item.Name, StringComparer.InvariantCultureIgnoreCase);
                    }
                }
            }
            var sign = sections.FirstOrDefault(s => s.IsSign);
            report.CustomerSignature = sign?.Items.FirstOrDefault()?.Pictures.FirstOrDefault()?.Bytes;
            var all = sections.SelectMany(s => s.Items).ToList();
            report.CustomerName = (all.FirstOrDefault(i => i.Number.Equals("Customer Name", StringComparison.OrdinalIgnoreCase)) ??
                                   all.FirstOrDefault(i => i.Name.ToUpperInvariant() == "CUSTOMER NAME"))?.Value ?? "";
            report.CustomerPhone = (all.FirstOrDefault(i => i.Number.Equals("Customer Phone", StringComparison.OrdinalIgnoreCase)) ??
                                    all.FirstOrDefault(i => i.Name.ToUpperInvariant() == "CUSTOMER PHONE"))?.Value ?? "";
        }

        // L24 indicators: (indicator, comments become action items).
        public static (string Indicator, bool Comments) Indicator(NhoVariant variant, string value)
        {
            bool form97 = variant == NhoVariant.Form97;
            return value switch
            {
                "Discussed" => ("check-green", !form97),
                "Yes" or "Pass" => ("check-green", false),
                "No" or "Fail" => form97 ? ("cross-red", true) : ("check-green", true),
                "NA" => ("cross-grey", false),
                _ => form97 ? ("", false) : ("check-green", true)
            };
        }

        public static double NumberKey(string number) =>
            double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double n) ? n : double.MaxValue;

        private static string S(JToken? t) => t == null || t.Type == JTokenType.Null ? "" : t.ToString();
        private static int I(JToken? t) => t != null && int.TryParse(t.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;
        private static bool B(JToken? t) => t != null && t.Type == JTokenType.Boolean && (bool)t;
        private static string? NullIfBlank(string s) => string.IsNullOrWhiteSpace(s) ? null : s;
        private static DateTime? Date(JToken? t) =>
            DateTimeOffset.TryParse(S(t), CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d.LocalDateTime : null;

        private static byte[]? Image(JToken? token)
        {
            string text = S(token);
            if (text.Length == 0) return null;
            try { return Convert.FromBase64String(text); }
            catch (FormatException) { return null; }
        }
    }
}
