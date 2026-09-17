using System;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using Newtonsoft.Json;
using InspectionEditor.Models;

namespace InspectionEditor.Services
{
    // Measured local results, deliberately separate from EC design/limit suggestions.
    public static class HetMeasuredTransferService
    {
        public sealed record Proposal(string? Value, string Source, string Detail, string Revision)
        {
            public bool CanApply => Value != null;
        }
        private static string Text(object? value) => value?.ToString()?.Trim() ?? "";
        private static bool Equal(string? a, string b) => string.Equals(a?.Trim(), b, StringComparison.OrdinalIgnoreCase);
        private static Item? Row(InspectionFile file, string number, string name) =>
            file.Sections?.Where(s => Equal(s.Name, Equal(file.InspectionCode, "AFI") ? "Evaluation of the total duct leakage" : "Duct System Test")).SelectMany(s => s.Items ?? new()).Where(i => i.Number == number && Equal(i.Name, name)).ToArray() is { Length: 1 } rows ? rows[0] : null;
        public static bool IsTarget(InspectionFile? file, Item item) => Equal(file?.InspectionCode, "AFI") && item.Number switch
        {
            "1.6" => Equal(item.Name, "Enter the total duct leakage of the system (CFM) - Unit 1"),
            "1.7" => Equal(item.Name, "Enter the total duct leakage of the system (CFM) - Unit 2"),
            "1.8" => Equal(item.Name, "Enter the total duct leakage percentage"),
            "1.9" => Equal(item.Name, "Total duct leakage pass/fail"),
            _ => false
        };
        private const string Number = @"(?:\d{1,3}(?:,\d{3})+|\d+)(?:\.\d+)?";
        public static string? Measurement(object? raw)
        {
            string text = Text(raw);
            if (Equal(text, "NI")) return "NI";
            var m = Regex.Match(text, "^(?<value>" + Number + @")\s*(?:CFM)?(?:\s+of\s*" + Number + @"\s*(?:CFM)?(?:\s+(?:FAILS?|PASS(?:ES)?))?)?$", RegexOptions.IgnoreCase);
            return m.Success && decimal.TryParse(m.Groups["value"].Value, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal n)
                ? n.ToString("0.################", CultureInfo.InvariantCulture) : null;
        }
        private static bool Numeric(object? raw, out decimal value) => decimal.TryParse(Measurement(raw), NumberStyles.Number, CultureInfo.InvariantCulture, out value);
        private static bool Identity(string path, InspectionFile file, string code, out string job)
        {
            job = "";
            var m = Regex.Match(Path.GetFileName(path), @"^(\d+)-" + code + @"-\d+-[A-Za-z0-9]+\.ins$", RegexOptions.IgnoreCase);
            if (!m.Success || !Equal(file.InspectionCode, code)) return false;
            job = m.Groups[1].Value;
            if (!string.IsNullOrWhiteSpace(file.InspectionNumber) && !Equal(file.InspectionNumber, Path.GetFileNameWithoutExtension(path))) return false;
            if (file.ExtensionData?.TryGetValue("JobNumber", out var meta) == true && !string.IsNullOrWhiteSpace(Text(meta)) && Text(meta) != job) return false;
            return true;
        }
        private static Proposal Calculate(InspectionFile target, Item? total2, string provenance, string revision)
        {
            Proposal Block(string why) => new(null, provenance, why, revision);
            var area = Row(target, "1.5", "Enter conditioned floor area");
            var unit1 = Row(target, "1.6", "Enter the total duct leakage of the system (CFM) - Unit 1");
            var unit2 = Row(target, "1.7", "Enter the total duct leakage of the system (CFM) - Unit 2");
            // Explicit CFA-basis calculation: CFM25 per 100 ft², not percent of fan airflow.
            // ANSI/RESNET/ACCA/ICC 310, Tables 501.4.1(1)/501.4.2(1), uses this normalization:
            // https://codes.iccsafe.org/content/ICC3102025P1/chapter-5-task-2-evaluation-of-the-total-duct-leakage
            // AFI form 215 provides whole-house CFA; do not guess two-system area allocation.
            // This is an explicit new calculation option, not a pre-existing RED formula or grading rule.
            if (target.FormId != 215 || !(Equal(Measurement(unit2?.Value), "NI") || (string.IsNullOrWhiteSpace(Text(unit2?.Value)) && Equal(Measurement(total2?.Value), "NI"))) || (total2 != null && !Equal(Measurement(total2.Value), "NI")))
                return Block("Calculation needs form 215 and confirmed unit 2 NI (current AFI or HET with current AFI unit 2 blank); multi-system floor-area allocation is unknown.");
            if (!Numeric(unit1?.Value, out decimal cfm) || !Regex.IsMatch(Text(area?.Value), "^" + Number + "$") || !Numeric(area?.Value, out decimal cfa) || cfa <= 0)
                return Block("Calculation needs current 1.6 measured CFM and positive current 1.5 conditioned floor area; NI, negative or missing values cannot be calculated.");
            string value = (cfm / cfa * 100m).ToString("0.##", CultureInfo.InvariantCulture);
            string detail = $"Calculated from current AFI 1.6 ({cfm} CFM) / 1.5 ({cfa} ft²) × 100 = {value}. Unit 2 is recorded NI; no second-system leakage assumed. Normalized CFM25 per 100 ft² (CFA basis), not percent of blower airflow. No pass/fail inferred.";
            revision += "|" + Text(unit1?.Value) + "|" + Text(area?.Value) + "|" + Text(unit2?.Value);
            return new(value, "Current AFI calculation", detail, revision);
        }
        public static Proposal? Resolve(string? targetPath, InspectionFile? target, Item item)
        {
            if (target == null || !IsTarget(target, item) || !ReferenceEquals(Row(target, item.Number!, item.Name!), item)) return null;
            Proposal Block(string why) => new(null, "Local HET", why, "");
            if (targetPath == null || !Identity(targetPath, target, "AFI", out string job)) return Block("Target filename and report identity must agree.");
            try
            {
                string folder = Path.GetDirectoryName(Path.GetFullPath(targetPath))!;
                // Prefer the opened report's folder. For an active MyList AFI, a completed
                // HET may already be in Review. Never search Temp or historical Archive copies.
                string[] Candidates(string directory) => Directory.GetFiles(directory, "*.ins")
                    .Where(p => Regex.IsMatch(Path.GetFileName(p), "^" + Regex.Escape(job) + @"-HET-", RegexOptions.IgnoreCase)).ToArray();
                var paths = Candidates(folder);
                if (paths.Length == 0 && Equal(Path.GetFileName(folder), "MyList"))
                {
                    string review = Path.Combine(Path.GetDirectoryName(folder)!, "Review");
                    if (Directory.Exists(review)) paths = Candidates(review);
                }
                if (paths.Length == 0 && item.Number == "1.8") return Calculate(target, null, "Current AFI", "");
                if (paths.Length != 1) return Block(paths.Length == 0 ? "No same-job HET in this local folder." : "Multiple same-job HET reports. Resolve source ambiguity before copying.");
                byte[] bytes = File.ReadAllBytes(paths[0]);
                var source = JsonConvert.DeserializeObject<InspectionFile>(System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'));
                if (source == null || source.Sections == null || source.Sections.Any(s => s == null || s.Items == null || s.Items.Any(i => i == null)) || !Identity(paths[0], source, "HET", out string sourceJob) || sourceJob != job) return Block("HET filename and report metadata disagree.");
                if ((!string.IsNullOrWhiteSpace(source.Address) && !string.IsNullOrWhiteSpace(target.Address) && !Equal(source.Address, target.Address!)) ||
                    (!string.IsNullOrWhiteSpace(source.City) && !string.IsNullOrWhiteSpace(target.City) && !Equal(source.City, target.City!))) return Block("HET address/city differs from current report.");
                string revision = Convert.ToHexString(SHA256.HashData(bytes));
                string provenance = Path.GetFileName(Path.GetDirectoryName(paths[0])) + "/" + Path.GetFileName(paths[0]);
                var total1 = Row(source, "3.3", "Duct system #1 total leakage CFM");
                var total2 = Row(source, "3.4", "Duct system #2 total leakage CFM");
                string? value = null;
                string detail = "";
                if (item.Number is "1.6" or "1.7")
                {
                    var row = item.Number == "1.6" ? total1 : total2;
                    value = Measurement(row?.Value);
                    detail = $"HET {row?.Number}: {Text(row?.Value)}. Only the measured CFM is copied, never the 'of' limit.";
                }
                else if (item.Number == "1.9")
                {
                    var pass = Row(source, "3.7", "Duct blaster test Pass/Fail");
                    string recorded = Text(pass?.Value);
                    value = new[] { "Pass", "Fail", "NI" }.FirstOrDefault(v => Equal(v, recorded));
                    detail = "Recorded HET 3.7, not an independently verified total-leakage result. ";
                    bool conflict = Equal(value, "Pass") && new[] { total1, total2 }.Any(r => Regex.IsMatch(Text(r?.Value), @"\bFAILS?\b", RegexOptions.IgnoreCase));
                    if (conflict) detail += "WARNING: recorded Pass conflicts with an explicit total-leakage FAIL. ";
                    detail += $"Total unit 1: {Text(total1?.Value)}; unit 2: {Text(total2?.Value)}. Outside leakage unit 1: {Text(Row(source, "3.5", "Duct system #1 leakage to outside CFM")?.Value)}. HET Pass may refer to outside leakage, which is not the AFI total-leakage decision. Confirm applicability before copying. No pass/fail is inferred or changed automatically.";
                }
                else
                {
                    var explicitRows = source.Sections.Where(s => Equal(s.Name, "Duct System Test")).SelectMany(s => s.Items ?? new()).Where(i => Equal(i.Name, "Enter the total duct leakage percentage") || Equal(i.Name, "Total duct leakage percentage")).ToArray();
                    if (explicitRows.Length > 1) return Block("Multiple explicit HET percentage rows.");
                    if (explicitRows.Length == 1)
                    {
                        string raw = Text(explicitRows[0].Value);
                        value = Regex.IsMatch(raw, "^" + Number + @"\s*%?$") ? Measurement(raw.TrimEnd('%').Trim()) : null;
                        detail = $"Explicit HET {explicitRows[0].Number} percentage: {raw}.";
                    }
                    if (value == null) return Calculate(target, total2, provenance, revision);
                }
                return new(value, provenance, value == null ? "No unambiguous recorded value. " + detail : detail, revision);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is JsonException || ex is OverflowException || ex is ArgumentException)
            { return Block("Local HET unavailable or invalid. Refresh and retry; no value copied."); }
        }
    }
}
